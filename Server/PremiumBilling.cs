using Stripe;
using Stripe.Checkout;

namespace TwoGether.Server;

public sealed class StripeBillingOptions
{
	public string SecretKey { get; set; } = "";
	public string PremiumPriceId { get; set; } = "";
	public string WebhookSecret { get; set; } = "";

	public bool IsConfigured =>
		!string.IsNullOrWhiteSpace(SecretKey)
		&& !string.IsNullOrWhiteSpace(PremiumPriceId)
		&& !string.IsNullOrWhiteSpace(WebhookSecret);
}

public sealed record PremiumOfferResponse(
	bool Available,
	long? AmountMinorUnits,
	string Currency,
	string Interval,
	int IntervalCount);

public sealed record PremiumStatusResponse(
	bool HasPremium,
	DateTimeOffset? AccessUntil,
	bool CancelAtPeriodEnd,
	bool CanManageBilling,
	bool CheckoutPending,
	bool SubscriptionPending);

public sealed record PremiumCheckoutSession(string Id, string Url);

public sealed record PremiumArticle(
	string Id,
	string Title,
	string Introduction,
	IReadOnlyList<string> Paragraphs);

public sealed class StripeBillingService
{
	private readonly StripeBillingOptions options;
	private readonly SemaphoreSlim offerLock = new(1, 1);
	private PremiumOfferResponse? cachedOffer;
	private DateTimeOffset offerExpiresAt;

	public StripeBillingService(StripeBillingOptions options)
	{
		this.options = options;
	}

	public async Task<PremiumOfferResponse> GetOfferAsync(CancellationToken cancellationToken)
	{
		if (!options.IsConfigured) return new PremiumOfferResponse(false, null, "eur", "month", 1);
		if (cachedOffer is not null && offerExpiresAt > DateTimeOffset.UtcNow) return cachedOffer;

		await offerLock.WaitAsync(cancellationToken);
		try
		{
			if (cachedOffer is not null && offerExpiresAt > DateTimeOffset.UtcNow) return cachedOffer;
			var price = await new PriceService(CreateClient()).GetAsync(
				options.PremiumPriceId,
				cancellationToken: cancellationToken);
			if (!price.Active
				|| price.Currency != "eur"
				|| price.Recurring?.Interval != "month"
				|| price.Recurring.IntervalCount != 1
				|| price.UnitAmount is not > 0)
				throw new InvalidOperationException("Le prix Premium Stripe doit être actif, mensuel, en euros et avoir un montant positif.");

			cachedOffer = new PremiumOfferResponse(true, price.UnitAmount, price.Currency, price.Recurring.Interval, 1);
			offerExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
			return cachedOffer;
		}
		finally
		{
			offerLock.Release();
		}
	}

	public async Task<PremiumCheckoutSession> CreateCheckoutSessionAsync(
		string userId,
		string email,
		Uri publicOrigin,
		CancellationToken cancellationToken)
	{
		EnsureConfigured();
		var session = await new SessionService(CreateClient()).CreateAsync(new SessionCreateOptions
		{
			Mode = "subscription",
			SuccessUrl = new Uri(publicOrigin, "/premium?checkout=success").ToString(),
			CancelUrl = new Uri(publicOrigin, "/premium?checkout=cancelled").ToString(),
			ExpiresAt = DateTime.UtcNow.AddMinutes(30),
			ClientReferenceId = userId,
			CustomerEmail = email,
			Metadata = new Dictionary<string, string> { ["userId"] = userId },
			LineItems =
			[
				new SessionLineItemOptions { Price = options.PremiumPriceId, Quantity = 1 }
			],
			SubscriptionData = new SessionSubscriptionDataOptions
			{
				Metadata = new Dictionary<string, string> { ["userId"] = userId }
			},
			AutomaticTax = new SessionAutomaticTaxOptions { Enabled = true }
		}, cancellationToken: cancellationToken);

		if (!Uri.TryCreate(session.Url, UriKind.Absolute, out var checkoutUri) || checkoutUri.Scheme != Uri.UriSchemeHttps)
			throw new StripeException("Stripe n’a pas retourné une URL de paiement HTTPS valide.");
		return new PremiumCheckoutSession(session.Id, checkoutUri.ToString());
	}

	public async Task<string> CreateBillingPortalSessionAsync(
		string customerId,
		Uri publicOrigin,
		CancellationToken cancellationToken)
	{
		EnsureConfigured();
		var session = await new Stripe.BillingPortal.SessionService(CreateClient()).CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
		{
			Customer = customerId,
			ReturnUrl = new Uri(publicOrigin, "/premium").ToString()
		}, cancellationToken: cancellationToken);
		if (!Uri.TryCreate(session.Url, UriKind.Absolute, out var portalUri) || portalUri.Scheme != Uri.UriSchemeHttps)
			throw new StripeException("Stripe n’a pas retourné une URL de gestion HTTPS valide.");
		return portalUri.ToString();
	}

	public async Task CancelSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
	{
		EnsureConfigured();
		await new SubscriptionService(CreateClient()).CancelAsync(subscriptionId, cancellationToken: cancellationToken);
	}

	public async Task ExpireOrCancelCheckoutAsync(string sessionId, CancellationToken cancellationToken)
	{
		EnsureConfigured();
		var service = new SessionService(CreateClient());
		var session = await service.GetAsync(sessionId, cancellationToken: cancellationToken);
		if (session.Status == "open")
		{
			await service.ExpireAsync(sessionId, cancellationToken: cancellationToken);
			return;
		}
		if (session.Status == "complete" && !string.IsNullOrWhiteSpace(session.SubscriptionId))
			await CancelSubscriptionAsync(session.SubscriptionId, cancellationToken);
	}

	private StripeClient CreateClient() => new(options.SecretKey);

	private void EnsureConfigured()
	{
		if (!options.IsConfigured)
			throw new InvalidOperationException("La facturation Premium n’est pas configurée.");
	}
}

public static class PremiumContentCatalog
{
	public static IReadOnlyList<PremiumArticle> Articles { get; } =
	[
		new PremiumArticle(
			"premier-rendez-vous",
			"Un premier rendez-vous qui te ressemble",
			"Choisir un cadre simple et agréable aide à laisser la conversation prendre sa place.",
			[
				"Privilégie un lieu public où vous pourrez parler sans vous sentir pressés : un café calme, une promenade fréquentée ou une activité courte.",
				"Propose une durée souple et garde la possibilité de prolonger si vous en avez envie. Un premier rendez-vous n’a pas besoin d’être une journée entière.",
				"Préviens une personne de confiance de l’endroit choisi et organise ton propre trajet. La sécurité et le confort passent avant la politesse."
			]),
		new PremiumArticle(
			"conversation-authentique",
			"Faire connaissance sans interrogatoire",
			"Une bonne conversation alterne curiosité, écoute et partage de soi.",
			[
				"Pars d’un détail que la personne a choisi de partager, puis pose une question ouverte. « Qu’est-ce qui te plaît dans cette activité ? » invite davantage à raconter qu’une suite de questions fermées.",
				"Partage aussi ta propre expérience. L’échange devient plus naturel quand chacun peut apporter quelque chose sans devoir se justifier.",
				"Respecte les sujets que l’autre préfère garder pour plus tard. La confiance se construit avec le temps, pas sous la pression."
			]),
		new PremiumArticle(
			"limites-et-respect",
			"Exprimer ses limites avec bienveillance",
			"Dire ce qui te convient et entendre la réponse de l’autre rend les échanges plus clairs.",
			[
				"Tu peux dire simplement ce que tu souhaites ou ne souhaites pas. Une limite n’a pas besoin d’être longue pour être valable.",
				"Un « non », une absence de réponse ou une demande d’espace doivent être respectés. N’insiste pas et n’essaie pas de contourner un blocage.",
				"Si une interaction te met mal à l’aise, interromps-la, bloque le compte si nécessaire et signale un comportement préoccupant."
			])
	];
}

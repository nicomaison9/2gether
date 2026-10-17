using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Stripe;
using Stripe.Checkout;

namespace TwoGether.Server;

public static class PremiumBillingApi
{
	private static readonly TimeSpan CheckoutReservationLifetime = TimeSpan.FromMinutes(45);

	public static void MapPremiumBillingApi(this WebApplication app)
	{
		app.MapGet("/api/billing/offer", GetOfferAsync).AllowAnonymous().RequireRateLimiting("billing-public");
		app.MapGet("/api/billing/status", GetStatusAsync).RequireAuthorization();
		app.MapPost("/api/billing/checkout", CreateCheckoutAsync).RequireAuthorization().RequireRateLimiting("billing");
		app.MapPost("/api/billing/portal", CreatePortalAsync).RequireAuthorization().RequireRateLimiting("billing");
		app.MapGet("/api/premium/content", GetPremiumContentAsync).RequireAuthorization();
		app.MapPost("/api/billing/webhook", HandleStripeWebhookAsync)
			.AllowAnonymous()
			.DisableAntiforgery()
			.WithMetadata(new RequestSizeLimitAttribute(1_048_576));
	}

	private static async Task<IResult> GetOfferAsync(
		StripeBillingService billing,
		ILoggerFactory loggerFactory,
		CancellationToken cancellationToken)
	{
		try
		{
			return Results.Ok(await billing.GetOfferAsync(cancellationToken));
		}
		catch (StripeException exception)
		{
			loggerFactory.CreateLogger("TwoGether.Billing").LogError(exception, "Unable to retrieve the Premium Stripe price.");
			return Results.Problem("L’offre Premium est momentanément indisponible.", statusCode: StatusCodes.Status503ServiceUnavailable);
		}
		catch (HttpRequestException exception)
		{
			loggerFactory.CreateLogger("TwoGether.Billing").LogError(exception, "Stripe could not be reached while retrieving the Premium price.");
			return Results.Problem("L’offre Premium est momentanément indisponible.", statusCode: StatusCodes.Status503ServiceUnavailable);
		}
		catch (InvalidOperationException exception)
		{
			loggerFactory.CreateLogger("TwoGether.Billing").LogError(exception, "The configured Premium Stripe price is invalid.");
			return Results.Problem("L’offre Premium n’est pas correctement configurée.", statusCode: StatusCodes.Status503ServiceUnavailable);
		}
	}

	private static async Task<IResult> GetStatusAsync(
		ClaimsPrincipal principal,
		AppDbContext db,
		CancellationToken cancellationToken)
	{
		var userId = GetUserId(principal);
		var now = DateTimeOffset.UtcNow;
		var subscription = await db.PremiumSubscriptions.AsNoTracking()
			.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
		var reservation = await db.PremiumCheckoutReservations.AsNoTracking()
			.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
		var hasPremium = subscription is not null
			&& subscription.Status is "active" or "trialing"
			&& subscription.CurrentPeriodEnd > now;
		var subscriptionPending = subscription?.Status == "incomplete"
			&& subscription.UpdatedAt > now.Subtract(CheckoutReservationLifetime);
		return Results.Ok(new PremiumStatusResponse(
			hasPremium,
			hasPremium ? subscription!.CurrentPeriodEnd : null,
			hasPremium && subscription!.CancelAtPeriodEnd,
			!string.IsNullOrWhiteSpace(subscription?.StripeCustomerId),
			reservation?.ExpiresAt > now,
			subscriptionPending));
	}

	private static async Task<IResult> GetPremiumContentAsync(
		ClaimsPrincipal principal,
		AppDbContext db,
		CancellationToken cancellationToken)
	{
		var userId = GetUserId(principal);
		var now = DateTimeOffset.UtcNow;
		var subscription = await db.PremiumSubscriptions.AsNoTracking()
			.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
		var hasPremium = subscription is not null
			&& (subscription.Status is "active" or "trialing")
			&& subscription.CurrentPeriodEnd > now;
		return hasPremium
			? Results.Ok(PremiumContentCatalog.Articles)
			: Results.Json(new { error = "Un abonnement Premium actif est nécessaire pour consulter ces contenus." }, statusCode: StatusCodes.Status403Forbidden);
	}

	private static async Task<IResult> CreateCheckoutAsync(
		ClaimsPrincipal principal,
		AppDbContext db,
		UserManager<AppUser> users,
		StripeBillingOptions options,
		EmailDeliveryOptions emailOptions,
		StripeBillingService billing,
		ILoggerFactory loggerFactory,
		HttpContext context)
	{
		if (!options.IsConfigured)
			return Results.Problem("Les paiements Premium ne sont pas configurés.", statusCode: StatusCodes.Status503ServiceUnavailable);

		var user = await users.GetUserAsync(principal);
		if (user is null || string.IsNullOrWhiteSpace(user.Email)) return Results.Unauthorized();
		var now = DateTimeOffset.UtcNow;
		var existingSubscription = await db.PremiumSubscriptions.AsNoTracking()
			.SingleOrDefaultAsync(item => item.UserId == user.Id, context.RequestAborted);
		if (existingSubscription is not null
			&& existingSubscription.Status is not ("canceled" or "incomplete_expired"))
			return Results.Conflict(new { error = "Un abonnement Premium est déjà actif ou en cours de confirmation." });

		var publicOrigin = new Uri(emailOptions.PublicOrigin, UriKind.Absolute);
		PremiumCheckoutReservation reservation;
		await using (var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted))
		{
			reservation = await db.PremiumCheckoutReservations.SingleOrDefaultAsync(
				item => item.UserId == user.Id,
				context.RequestAborted) ?? new PremiumCheckoutReservation { UserId = user.Id };
			if (reservation.ExpiresAt > now)
				return Results.Conflict(new { error = "Un paiement est déjà en cours pour ce compte." });

			reservation.CreatedAt = now;
			reservation.ExpiresAt = now.Add(CheckoutReservationLifetime);
			reservation.StripeSessionId = null;
			if (db.Entry(reservation).State == EntityState.Detached)
				db.PremiumCheckoutReservations.Add(reservation);
			else
				db.PremiumCheckoutReservations.Update(reservation);

			try
			{
				await db.SaveChangesAsync(context.RequestAborted);
				await transaction.CommitAsync(context.RequestAborted);
			}
			catch (DbUpdateException exception)
			{
				await transaction.RollbackAsync(context.RequestAborted);
				loggerFactory.CreateLogger("TwoGether.Billing").LogWarning(exception, "A concurrent Premium checkout reservation was rejected.");
				return Results.Conflict(new { error = "Un paiement est déjà en cours pour ce compte." });
			}
		}

		try
		{
			var offer = await billing.GetOfferAsync(context.RequestAborted);
			if (!offer.Available)
			{
				await RemoveReservationAsync(db, user.Id, context.RequestAborted);
				return Results.Problem("L’offre Premium n’est pas disponible.", statusCode: StatusCodes.Status503ServiceUnavailable);
			}

			var checkout = await billing.CreateCheckoutSessionAsync(
				user.Id,
				user.Email,
				publicOrigin,
				context.RequestAborted);
			reservation.StripeSessionId = checkout.Id;
			await db.SaveChangesAsync(context.RequestAborted);
			return Results.Ok(new { url = checkout.Url });
		}
		catch (StripeException exception)
		{
			await RemoveReservationAsync(db, user.Id, context.RequestAborted);
			loggerFactory.CreateLogger("TwoGether.Billing").LogError(exception, "Stripe could not create a Premium checkout session.");
			return Results.Problem("Le paiement n’a pas pu être préparé. Réessaie dans un instant.", statusCode: StatusCodes.Status502BadGateway);
		}
		catch (HttpRequestException exception)
		{
			await RemoveReservationAsync(db, user.Id, context.RequestAborted);
			loggerFactory.CreateLogger("TwoGether.Billing").LogError(exception, "Stripe could not be reached while creating a Premium checkout session.");
			return Results.Problem("Le paiement n’a pas pu être préparé. Réessaie dans un instant.", statusCode: StatusCodes.Status502BadGateway);
		}
		catch (InvalidOperationException exception)
		{
			await RemoveReservationAsync(db, user.Id, context.RequestAborted);
			loggerFactory.CreateLogger("TwoGether.Billing").LogError(exception, "Premium checkout configuration is invalid.");
			return Results.Problem("Le paiement n’a pas pu être préparé. Réessaie dans un instant.", statusCode: StatusCodes.Status503ServiceUnavailable);
		}
	}

	private static async Task<IResult> CreatePortalAsync(
		ClaimsPrincipal principal,
		AppDbContext db,
		EmailDeliveryOptions emailOptions,
		StripeBillingOptions options,
		StripeBillingService billing,
		ILoggerFactory loggerFactory,
		HttpContext context)
	{
		if (!options.IsConfigured)
			return Results.Problem("La gestion de facturation n’est pas configurée.", statusCode: StatusCodes.Status503ServiceUnavailable);
		var userId = GetUserId(principal);
		var subscription = await db.PremiumSubscriptions.AsNoTracking()
			.SingleOrDefaultAsync(item => item.UserId == userId, context.RequestAborted);
		if (string.IsNullOrWhiteSpace(subscription?.StripeCustomerId)) return Results.NotFound();
		try
		{
			var url = await billing.CreateBillingPortalSessionAsync(
				subscription.StripeCustomerId,
				new Uri(emailOptions.PublicOrigin, UriKind.Absolute),
				context.RequestAborted);
			return Results.Ok(new { url });
		}
		catch (StripeException exception)
		{
			loggerFactory.CreateLogger("TwoGether.Billing").LogError(exception, "Stripe could not create a billing portal session for user {UserId}.", userId);
			return Results.Problem("La gestion de l’abonnement est momentanément indisponible.", statusCode: StatusCodes.Status502BadGateway);
		}
		catch (HttpRequestException exception)
		{
			loggerFactory.CreateLogger("TwoGether.Billing").LogError(exception, "Stripe could not be reached while creating a billing portal session for user {UserId}.", userId);
			return Results.Problem("La gestion de l’abonnement est momentanément indisponible.", statusCode: StatusCodes.Status502BadGateway);
		}
	}

	private static async Task<IResult> HandleStripeWebhookAsync(
		HttpContext context,
		AppDbContext db,
		StripeBillingOptions options,
		StripeBillingService billing,
		ILoggerFactory loggerFactory)
	{
		if (!options.IsConfigured)
			return Results.Problem("Le point d’entrée Stripe n’est pas configuré.", statusCode: StatusCodes.Status503ServiceUnavailable);
		if (context.Request.ContentLength is > 1_048_576)
			return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

		string payload;
		using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
			payload = await reader.ReadToEndAsync(context.RequestAborted);
		if (Encoding.UTF8.GetByteCount(payload) > 1_048_576)
			return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

		Event stripeEvent;
		try
		{
			stripeEvent = EventUtility.ConstructEvent(
				payload,
				context.Request.Headers["Stripe-Signature"].ToString(),
				options.WebhookSecret,
				throwOnApiVersionMismatch: false);
		}
		catch (StripeException exception)
		{
			loggerFactory.CreateLogger("TwoGether.Billing").LogWarning(exception, "A Stripe webhook with an invalid signature or payload was rejected.");
			return Results.BadRequest(new { error = "Signature Stripe invalide." });
		}
		catch (JsonException exception)
		{
			loggerFactory.CreateLogger("TwoGether.Billing").LogWarning(exception, "A malformed Stripe webhook was rejected.");
			return Results.BadRequest(new { error = "Notification Stripe invalide." });
		}

		if (stripeEvent.Type == "checkout.session.expired"
			&& stripeEvent.Data.Object is Stripe.Checkout.Session expiredSession
			&& !string.IsNullOrWhiteSpace(expiredSession.ClientReferenceId))
		{
			await db.PremiumCheckoutReservations
				.Where(item => item.UserId == expiredSession.ClientReferenceId)
				.ExecuteDeleteAsync(context.RequestAborted);
			return Results.NoContent();
		}

		if (stripeEvent.Type == "checkout.session.completed"
			&& stripeEvent.Data.Object is Stripe.Checkout.Session completedSession
			&& !string.IsNullOrWhiteSpace(completedSession.SubscriptionId))
		{
			await ApplyCheckoutCompletedEventAsync(
				stripeEvent,
				completedSession,
				db,
				billing,
				loggerFactory,
				context.RequestAborted);
			return Results.NoContent();
		}

		if (!stripeEvent.Type.StartsWith("customer.subscription.", StringComparison.Ordinal))
			return Results.NoContent();
		if (stripeEvent.Data.Object is not Subscription stripeSubscription)
			return Results.BadRequest(new { error = "Objet d’abonnement Stripe invalide." });

		await ApplySubscriptionEventAsync(stripeEvent, stripeSubscription, db, loggerFactory, context.RequestAborted);
		return Results.NoContent();
	}

	private static async Task ApplyCheckoutCompletedEventAsync(
		Event stripeEvent,
		Stripe.Checkout.Session session,
		AppDbContext db,
		StripeBillingService billing,
		ILoggerFactory loggerFactory,
		CancellationToken cancellationToken)
	{
		var logger = loggerFactory.CreateLogger("TwoGether.Billing");
		var userId = session.ClientReferenceId;
		if (string.IsNullOrWhiteSpace(userId)) return;
		if (!await db.Users.AnyAsync(user => user.Id == userId, cancellationToken))
		{
			logger.LogWarning("Completed Stripe checkout {EventId} belongs to a deleted 2gether account; cancelling its subscription.", stripeEvent.Id);
			await billing.CancelSubscriptionAsync(session.SubscriptionId!, cancellationToken);
			return;
		}

		var eventCreatedAt = new DateTimeOffset(DateTime.SpecifyKind(stripeEvent.Created, DateTimeKind.Utc));
		await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
		if (await db.StripeWebhookReceipts.AnyAsync(receipt => receipt.EventId == stripeEvent.Id, cancellationToken))
		{
			await transaction.RollbackAsync(cancellationToken);
			return;
		}

		var subscription = await db.PremiumSubscriptions.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
		if (subscription is null)
		{
			subscription = new PremiumSubscription { UserId = userId };
			db.PremiumSubscriptions.Add(subscription);
		}
		if (subscription.StripeSubscriptionId != session.SubscriptionId
			&& eventCreatedAt >= subscription.LastStripeEventCreatedAt)
		{
			subscription.StripeCustomerId = session.CustomerId ?? "";
			subscription.StripeSubscriptionId = session.SubscriptionId!;
			subscription.Status = "incomplete";
			subscription.CurrentPeriodEnd = null;
			subscription.CancelAtPeriodEnd = false;
			subscription.LastStripeEventCreatedAt = eventCreatedAt;
			subscription.UpdatedAt = DateTimeOffset.UtcNow;
		}
		db.StripeWebhookReceipts.Add(new StripeWebhookReceipt { EventId = stripeEvent.Id, UserId = userId });
		await db.PremiumCheckoutReservations.Where(item => item.UserId == userId).ExecuteDeleteAsync(cancellationToken);
		await SaveStripeReceiptAsync(db, stripeEvent.Id, transaction, cancellationToken);
	}

	private static async Task ApplySubscriptionEventAsync(
		Event stripeEvent,
		Subscription stripeSubscription,
		AppDbContext db,
		ILoggerFactory loggerFactory,
		CancellationToken cancellationToken)
	{
		var logger = loggerFactory.CreateLogger("TwoGether.Billing");
		var mappedSubscription = await db.PremiumSubscriptions.AsNoTracking()
			.SingleOrDefaultAsync(item => item.StripeSubscriptionId == stripeSubscription.Id, cancellationToken);
		var metadataUserId = stripeSubscription.Metadata?.GetValueOrDefault("userId");
		var userId = metadataUserId ?? mappedSubscription?.UserId;
		if (string.IsNullOrWhiteSpace(userId)
			|| !await db.Users.AnyAsync(user => user.Id == userId, cancellationToken))
		{
			logger.LogWarning("Stripe subscription event {EventId} has no matching 2gether account.", stripeEvent.Id);
			return;
		}
		if (mappedSubscription is not null && mappedSubscription.UserId != userId)
		{
			logger.LogError("Stripe subscription {SubscriptionId} is mapped to conflicting 2gether accounts.", stripeSubscription.Id);
			return;
		}

		var eventCreatedAt = new DateTimeOffset(DateTime.SpecifyKind(stripeEvent.Created, DateTimeKind.Utc));
		var periodEndDate = stripeSubscription.Items?.Data
			.OrderByDescending(item => item.CurrentPeriodEnd)
			.Select(item => (DateTime?)item.CurrentPeriodEnd)
			.FirstOrDefault();
		var periodEnd = periodEndDate is DateTime end
			? new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc))
			: (DateTimeOffset?)null;

		await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
		if (await db.StripeWebhookReceipts.AnyAsync(receipt => receipt.EventId == stripeEvent.Id, cancellationToken))
		{
			await transaction.RollbackAsync(cancellationToken);
			return;
		}

		var subscription = await db.PremiumSubscriptions.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
		var isNewerEvent = subscription is null || eventCreatedAt >= subscription.LastStripeEventCreatedAt;
		if (isNewerEvent)
		{
			subscription ??= new PremiumSubscription { UserId = userId };
			subscription.StripeCustomerId = stripeSubscription.CustomerId ?? "";
			subscription.StripeSubscriptionId = stripeSubscription.Id;
			subscription.Status = stripeSubscription.Status ?? "unknown";
			subscription.CurrentPeriodEnd = periodEnd;
			subscription.CancelAtPeriodEnd = stripeSubscription.CancelAtPeriodEnd;
			subscription.LastStripeEventCreatedAt = eventCreatedAt;
			subscription.UpdatedAt = DateTimeOffset.UtcNow;
			if (db.Entry(subscription).State == EntityState.Detached)
				db.PremiumSubscriptions.Add(subscription);
		}

		db.StripeWebhookReceipts.Add(new StripeWebhookReceipt { EventId = stripeEvent.Id, UserId = userId });
		await db.PremiumCheckoutReservations.Where(item => item.UserId == userId).ExecuteDeleteAsync(cancellationToken);
		await SaveStripeReceiptAsync(db, stripeEvent.Id, transaction, cancellationToken);
	}

	private static async Task SaveStripeReceiptAsync(
		AppDbContext db,
		string eventId,
		Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
		CancellationToken cancellationToken)
	{
		try
		{
			await db.SaveChangesAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
		}
		catch (DbUpdateException)
		{
			await transaction.RollbackAsync(cancellationToken);
			if (!await db.StripeWebhookReceipts.AsNoTracking().AnyAsync(receipt => receipt.EventId == eventId, cancellationToken))
				throw;
		}
	}

	private static async Task RemoveReservationAsync(AppDbContext db, string userId, CancellationToken cancellationToken)
	{
		await db.PremiumCheckoutReservations
			.Where(item => item.UserId == userId)
			.ExecuteDeleteAsync(cancellationToken);
	}

	private static string GetUserId(ClaimsPrincipal principal) =>
		principal.FindFirstValue(ClaimTypes.NameIdentifier)
		?? throw new InvalidOperationException("L’utilisateur authentifié ne possède aucun identifiant.");
}

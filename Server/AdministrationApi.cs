using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace TwoGether.Server;

public sealed class AdministratorAccess(IEnumerable<string> emails)
{
	private readonly HashSet<string> administratorEmails = emails
		.Where(email => !string.IsNullOrWhiteSpace(email))
		.Select(email => email.Trim())
		.ToHashSet(StringComparer.OrdinalIgnoreCase);

	public bool IsConfigured => administratorEmails.Count > 0;

	public bool Allows(string? email) =>
		!string.IsNullOrWhiteSpace(email) && administratorEmails.Contains(email);
}

public sealed record AdministrationOverviewResponse(
	int ConfirmedMembers,
	int PendingReports,
	int SuspendedMembers,
	bool StripeConfigured,
	bool PremiumSalesEnabled,
	bool AdvertisingIntegrationAvailable);

public sealed record AdministrationMonetizationResponse(
	bool CoreFeaturesRemainFree,
	bool PremiumSalesEnabled,
	bool StripeConfigured,
	bool AdvertisingEnabled,
	bool AdvertisingIntegrationAvailable,
	DateTimeOffset? UpdatedAt);

public sealed record AdministrationAuditResponse(
	DateTimeOffset CreatedAt,
	string ActorUserId,
	string Action,
	string Details);

public sealed record UpdateAdministrationMonetizationRequest(bool PremiumSalesEnabled);

public static class AdministrationApi
{
	public const int MonetizationSettingsId = 1;

	public static void MapAdministrationApi(this WebApplication app)
	{
		var administration = app.MapGroup("/api/administration")
			.RequireAuthorization("administrator")
			.RequireRateLimiting("administration");

		administration.MapGet("/overview", GetOverviewAsync);
		administration.MapGet("/monetization", GetMonetizationAsync);
		administration.MapPut("/monetization", UpdateMonetizationAsync);
		administration.MapGet("/audit", GetAuditAsync);
	}

	private static async Task<IResult> GetOverviewAsync(
		AppDbContext db,
		StripeBillingOptions stripe,
		CancellationToken cancellationToken)
	{
		var settings = await db.MonetizationSettings.AsNoTracking()
			.SingleOrDefaultAsync(item => item.Id == MonetizationSettingsId, cancellationToken);
		var confirmedMembers = await db.Users.CountAsync(user => user.EmailConfirmed, cancellationToken);
		var pendingReports = await db.AbuseReports.CountAsync(report => report.Status == AbuseReportStatus.Pending, cancellationToken);
		var suspendedMembers = await db.Users.CountAsync(user => user.IsSuspended, cancellationToken);
		return Results.Ok(new AdministrationOverviewResponse(
			confirmedMembers,
			pendingReports,
			suspendedMembers,
			stripe.IsConfigured,
			settings?.PremiumSalesEnabled == true,
			AdvertisingIntegrationAvailable: false));
	}

	private static async Task<IResult> GetMonetizationAsync(
		AppDbContext db,
		StripeBillingOptions stripe,
		CancellationToken cancellationToken)
	{
		var settings = await db.MonetizationSettings.AsNoTracking()
			.SingleOrDefaultAsync(item => item.Id == MonetizationSettingsId, cancellationToken);
		return Results.Ok(new AdministrationMonetizationResponse(
			CoreFeaturesRemainFree: true,
			settings?.PremiumSalesEnabled == true,
			stripe.IsConfigured,
			AdvertisingEnabled: false,
			AdvertisingIntegrationAvailable: false,
			settings?.UpdatedAt));
	}

	private static async Task<IResult> UpdateMonetizationAsync(
		[FromBody] UpdateAdministrationMonetizationRequest request,
		ClaimsPrincipal principal,
		AppDbContext db,
		StripeBillingOptions stripeOptions,
		StripeBillingService billing,
		ILoggerFactory loggerFactory,
		HttpContext context)
	{
		if (request.PremiumSalesEnabled)
		{
			if (!stripeOptions.IsConfigured)
				return Results.Conflict(new { error = "Configure Stripe dans l’environnement avant d’ouvrir les souscriptions." });

			try
			{
				var offer = await billing.GetOfferAsync(context.RequestAborted);
				if (!offer.Available)
					return Results.Conflict(new { error = "Aucune offre Stripe Premium valide n’est configurée." });
			}
			catch (Exception exception) when (exception is StripeException or HttpRequestException or InvalidOperationException)
			{
				loggerFactory.CreateLogger("TwoGether.Administration").LogError(exception, "Premium sales could not be enabled because the Stripe offer is unavailable.");
				return Results.Problem("Vérifie la configuration Stripe avant d’ouvrir les souscriptions.", statusCode: StatusCodes.Status503ServiceUnavailable);
			}
		}

		var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
		if (string.IsNullOrWhiteSpace(actorUserId)) return Results.Unauthorized();

		await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
		var settings = await db.MonetizationSettings.SingleOrDefaultAsync(
			item => item.Id == MonetizationSettingsId,
			context.RequestAborted);
		if (settings is null)
		{
			settings = new MonetizationSettings { Id = MonetizationSettingsId };
			db.MonetizationSettings.Add(settings);
		}

		var previousValue = settings.PremiumSalesEnabled;
		if (previousValue != request.PremiumSalesEnabled)
		{
			settings.PremiumSalesEnabled = request.PremiumSalesEnabled;
			settings.UpdatedAt = DateTimeOffset.UtcNow;
			settings.UpdatedByUserId = actorUserId;
			db.AdministrativeAuditEntries.Add(new AdministrativeAuditEntry
			{
				ActorUserId = actorUserId,
				Action = "monetization.premium-sales",
				Details = $"Premium sales changed from {previousValue} to {request.PremiumSalesEnabled}."
			});
		}

		await db.SaveChangesAsync(context.RequestAborted);
		await transaction.CommitAsync(context.RequestAborted);
		return await GetMonetizationAsync(db, stripeOptions, context.RequestAborted);
	}

	private static async Task<IResult> GetAuditAsync(
		AppDbContext db,
		CancellationToken cancellationToken)
	{
		var entries = await db.AdministrativeAuditEntries.AsNoTracking()
			.OrderByDescending(entry => entry.Id)
			.Take(50)
			.Select(entry => new AdministrationAuditResponse(
				entry.CreatedAt,
				entry.ActorUserId,
				entry.Action,
				entry.Details))
			.ToListAsync(cancellationToken);
		return Results.Ok(entries);
	}
}

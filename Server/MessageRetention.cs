using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TwoGether.Server;

public static class MessageRetentionPolicy
{
	public const int SettingsId = 1;
	public const int DefaultDays = 30;
	public const int MinimumDays = 1;
	public const int MaximumDays = 150;

	public static async Task<int> GetRetentionDaysAsync(
		AppDbContext db,
		CancellationToken cancellationToken = default)
	{
		var retentionDays = await db.MessageRetentionSettings.AsNoTracking()
			.Where(settings => settings.Id == SettingsId)
			.Select(settings => (int?)settings.RetentionDays)
			.SingleOrDefaultAsync(cancellationToken) ?? DefaultDays;
		if (retentionDays is < MinimumDays or > MaximumDays)
			throw new InvalidOperationException("The stored message retention period is outside the supported range.");
		return retentionDays;
	}

	public static async Task<DateTimeOffset> GetExpirationCutoffAsync(
		AppDbContext db,
		DateTimeOffset now,
		CancellationToken cancellationToken = default)
	{
		var retentionDays = await GetRetentionDaysAsync(db, cancellationToken);
		return now.ToUniversalTime().AddDays(-retentionDays);
	}

	public static async Task<int> DeleteExpiredMessagesAsync(
		AppDbContext db,
		DateTimeOffset now,
		CancellationToken cancellationToken = default)
	{
		var cutoff = await GetExpirationCutoffAsync(db, now, cancellationToken);
		var cutoffValue = cutoff.ToString("O", CultureInfo.InvariantCulture);
		return await db.Database.ExecuteSqlInterpolatedAsync(
			$"""DELETE FROM "DirectMessages" WHERE julianday("SentAt") < julianday({cutoffValue});""",
			cancellationToken);
	}
}

public sealed class MessageRetentionCleanupService(
	IServiceScopeFactory scopeFactory,
	ILogger<MessageRetentionCleanupService> logger) : BackgroundService
{
	private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var timer = new PeriodicTimer(CleanupInterval);
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await using var scope = scopeFactory.CreateAsyncScope();
				var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
				var deletedCount = await MessageRetentionPolicy.DeleteExpiredMessagesAsync(
					db,
					DateTimeOffset.UtcNow,
					stoppingToken);
				if (deletedCount > 0)
					logger.LogInformation("Automatically deleted {DeletedCount} expired direct messages.", deletedCount);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
			catch (SqliteException exception)
			{
				logger.LogError(exception, "Automatic message-retention cleanup failed; it will retry on the next interval.");
			}

			try
			{
				if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
		}
	}
}

using Microsoft.EntityFrameworkCore;

namespace TwoGether.Server;

public static class DatabaseMigrationRunner
{
	public static async Task MigrateAsync(AppDbContext database, CancellationToken cancellationToken = default)
	{
		var connection = database.Database.GetDbConnection();
		var wasOpen = connection.State == System.Data.ConnectionState.Open;
		try
		{
			if (!wasOpen)
				await connection.OpenAsync(cancellationToken);

			var hasUsersTable = await TableExistsAsync(connection, "AspNetUsers", cancellationToken);
			var hasHistoryTable = await TableExistsAsync(connection, "__EFMigrationsHistory", cancellationToken);
			var initialMigrationId = database.Database.GetMigrations().FirstOrDefault();
			var hasInitialMigration = hasHistoryTable && initialMigrationId is not null
				&& await MigrationExistsAsync(connection, initialMigrationId, cancellationToken);

			if (hasUsersTable && !hasInitialMigration)
				throw new InvalidOperationException(
					"Cette base SQLite existante ne possède pas l’historique EF Core attendu. Sauvegardez-la, vérifiez son schéma, puis suivez Server/Scripts/BaselineLegacySqlite.sql avant de redémarrer.");
		}
		finally
		{
			if (!wasOpen)
				await connection.CloseAsync();
		}

		await database.Database.MigrateAsync(cancellationToken);
	}

	private static async Task<bool> TableExistsAsync(
		System.Data.Common.DbConnection connection,
		string tableName,
		CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
		var parameter = command.CreateParameter();
		parameter.ParameterName = "$name";
		parameter.Value = tableName;
		command.Parameters.Add(parameter);
		return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 0;
	}

	private static async Task<bool> MigrationExistsAsync(
		System.Data.Common.DbConnection connection,
		string migrationId,
		CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT EXISTS (SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = $migrationId);";
		var parameter = command.CreateParameter();
		parameter.ParameterName = "$migrationId";
		parameter.Value = migrationId;
		command.Parameters.Add(parameter);
		return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 0;
	}
}

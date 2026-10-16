using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TwoGether.Server;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
	public AppDbContext CreateDbContext(string[] args)
	{
		var databasePath = Environment.GetEnvironmentVariable("TWOGETHER_DB_PATH")
			?? Path.Combine(Path.GetTempPath(), "2gether-design-time.db");
		var options = new DbContextOptionsBuilder<AppDbContext>()
			.UseSqlite($"Data Source={databasePath}")
			.Options;
		return new AppDbContext(options);
	}
}

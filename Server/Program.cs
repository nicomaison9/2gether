using System.Threading.RateLimiting;
using System.Security.Claims;
using Fido2NetLib;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Antiforgery;
using System.Net;
using TwoGether.Server;

var builder = WebApplication.CreateBuilder(args);

var emailOptions = builder.Configuration.GetSection("Email").Get<EmailDeliveryOptions>() ?? new();
if (!Uri.TryCreate(emailOptions.PublicOrigin, UriKind.Absolute, out var emailOrigin)
	|| !string.IsNullOrEmpty(emailOrigin.UserInfo) || emailOrigin.AbsolutePath != "/" || !string.IsNullOrEmpty(emailOrigin.Query) || !string.IsNullOrEmpty(emailOrigin.Fragment)
	|| (emailOrigin.Scheme != "https" && !(builder.Environment.IsDevelopment() && emailOrigin.IsLoopback && emailOrigin.Scheme == "http")))
	throw new InvalidOperationException("Configure Email:PublicOrigin with the public HTTPS origin (HTTP loopback is allowed only in Development).");
if (!MimeKit.MailboxAddress.TryParse(emailOptions.FromAddress, out _)
	|| emailOptions.Delivery is not ("Smtp" or "PickupDirectory")
	|| (emailOptions.Delivery == "PickupDirectory" && !builder.Environment.IsDevelopment())
	|| (emailOptions.Delivery == "Smtp" && (string.IsNullOrWhiteSpace(emailOptions.Host) || emailOptions.Port is < 1 or > 65535)))
	throw new InvalidOperationException("Configure Email delivery and FromAddress. PickupDirectory is allowed only in Development; SMTP requires a host and TLS port.");
var stripeBillingOptions = builder.Configuration.GetSection("Stripe").Get<StripeBillingOptions>() ?? new();
var stripeBillingValues = new[] { stripeBillingOptions.SecretKey, stripeBillingOptions.PremiumPriceId, stripeBillingOptions.WebhookSecret };
var configuredStripeBillingValues = stripeBillingValues.Count(value => !string.IsNullOrWhiteSpace(value));
if (configuredStripeBillingValues is > 0 and < 3)
	throw new InvalidOperationException("Configure Stripe:SecretKey, Stripe:PremiumPriceId and Stripe:WebhookSecret together, or leave billing disabled in Development.");
if (builder.Environment.IsProduction() && !stripeBillingOptions.IsConfigured)
	throw new InvalidOperationException("Configure Stripe billing before starting in Production.");
if (stripeBillingOptions.IsConfigured
	&& (!stripeBillingOptions.PremiumPriceId.StartsWith("price_", StringComparison.Ordinal)
		|| !stripeBillingOptions.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal)
		|| !stripeBillingOptions.SecretKey.StartsWith(builder.Environment.IsProduction() ? "sk_live_" : "sk_test_", StringComparison.Ordinal)))
	throw new InvalidOperationException("Use a Stripe price ID, webhook signing secret and API key for the current environment.");
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (builder.Environment.IsProduction()
	&& (string.IsNullOrWhiteSpace(dataProtectionKeysPath) || !Path.IsPathRooted(dataProtectionKeysPath)))
	throw new InvalidOperationException("Configure DataProtection:KeysPath as an absolute persistent path in Production.");
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
	dataProtectionKeysPath = Path.GetFullPath(dataProtectionKeysPath);
	Directory.CreateDirectory(dataProtectionKeysPath);
}
var trustedProxies = new List<IPAddress>();
foreach (var configuredAddress in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
{
	if (!IPAddress.TryParse(configuredAddress, out var address))
		throw new InvalidOperationException($"Invalid ForwardedHeaders:KnownProxies address: {configuredAddress}");
	trustedProxies.Add(address);
}
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
	options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
	options.ForwardLimit = 1;
	options.KnownProxies.Clear();
	options.KnownIPNetworks.Clear();
	foreach (var proxy in trustedProxies)
		options.KnownProxies.Add(proxy);
});
builder.Services.AddAntiforgery(options =>
{
	options.HeaderName = "X-CSRF-TOKEN";
	options.Cookie.Name = ".2gether.Antiforgery";
	options.Cookie.HttpOnly = true;
	options.Cookie.SameSite = SameSiteMode.Strict;
	options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
		? CookieSecurePolicy.SameAsRequest
		: CookieSecurePolicy.Always;
});
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("2gether");
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
	dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
builder.Services.AddSingleton(emailOptions);
builder.Services.AddSingleton(stripeBillingOptions);
builder.Services.AddSingleton<StripeBillingService>();
builder.Services.AddTransient<IEmailSender<AppUser>, VerificationEmailSender>();
builder.Services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(1));
builder.Services.AddSingleton<IFido2>(_ => new Fido2(new Fido2Configuration
{
	RPID = emailOrigin.Host,
	RPName = "2gether",
	Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { emailOrigin.GetLeftPart(UriPartial.Authority) }
}));
builder.Services.AddSingleton<PasskeyChallengeStore>();

ImageMagick.ResourceLimits.Memory = 128 * 1024 * 1024;
ImageMagick.ResourceLimits.Disk = 0;
ImageMagick.ResourceLimits.Thread = 2;

var databasePath = builder.Configuration["TWOGETHER_DB_PATH"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "2gether.db");
databasePath = Path.GetFullPath(databasePath);
var databaseDirectory = Path.GetDirectoryName(databasePath) ?? builder.Environment.ContentRootPath;
Directory.CreateDirectory(databaseDirectory);
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddIdentityApiEndpoints<AppUser>(options =>
{
	options.User.RequireUniqueEmail = true;
	options.SignIn.RequireConfirmedEmail = true;
	options.Password.RequiredLength = 10;
	options.Password.RequireDigit = true;
	options.Password.RequireUppercase = false;
	options.Password.RequireNonAlphanumeric = false;
	options.Lockout.MaxFailedAccessAttempts = 5;
	options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<AppDbContext>();
builder.Services.ConfigureApplicationCookie(options =>
{
	options.Cookie.HttpOnly = true;
	options.Cookie.SameSite = SameSiteMode.Lax;
	options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
	options.SlidingExpiration = true;
	options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
	options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
var moderatorEmails = (builder.Configuration.GetSection("Moderation:ModeratorEmails").Get<string[]>() ?? [])
	.Where(email => !string.IsNullOrWhiteSpace(email))
	.Select(email => email.Trim())
	.ToHashSet(StringComparer.OrdinalIgnoreCase);
if (builder.Environment.IsProduction() && moderatorEmails.Count == 0)
	throw new InvalidOperationException("Configure at least one confirmed moderator email with Moderation:ModeratorEmails in Production.");
var administratorAccess = new AdministratorAccess(
	(builder.Configuration.GetSection("Administration:AdministratorEmails").Get<string[]>() ?? [])
		.Where(email => !string.IsNullOrWhiteSpace(email)));
if (builder.Environment.IsProduction() && !administratorAccess.IsConfigured)
	throw new InvalidOperationException("Configure at least one confirmed administrator email with Administration:AdministratorEmails in Production.");
builder.Services.AddSingleton(administratorAccess);
builder.Services.AddAuthorization(options =>
{
	options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireAssertion(async context =>
	{
		if (context.Resource is not HttpContext httpContext) return false;
		var users = httpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
		var user = await users.GetUserAsync(context.User);
		return user is not null && user.EmailConfirmed && !user.IsSuspended;
	}).Build();
	options.AddPolicy("account-management", policy => policy
		.RequireAuthenticatedUser()
		.RequireAssertion(async context =>
		{
			if (context.Resource is not HttpContext httpContext) return false;
			var user = await httpContext.RequestServices.GetRequiredService<UserManager<AppUser>>().GetUserAsync(context.User);
			return user is not null && user.EmailConfirmed;
		}));
	options.AddPolicy("moderator", policy => policy
		.RequireAuthenticatedUser()
		.RequireAssertion(async context =>
		{
			if (context.Resource is not HttpContext httpContext) return false;
			var user = await httpContext.RequestServices.GetRequiredService<UserManager<AppUser>>().GetUserAsync(context.User);
			return user is not null && user.EmailConfirmed && !user.IsSuspended
				&& moderatorEmails.Contains(user.Email ?? "");
		}));
	options.AddPolicy("administrator", policy => policy
			.RequireAuthenticatedUser()
			.RequireAssertion(async context =>
			{
				if (context.Resource is not HttpContext httpContext) return false;
				var user = await httpContext.RequestServices.GetRequiredService<UserManager<AppUser>>().GetUserAsync(context.User);
				return user is not null && user.EmailConfirmed && !user.IsSuspended
					&& administratorAccess.Allows(user.Email);
			}));
});
builder.Services.AddRateLimiter(options =>
{
	options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
	options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
		Category(context.Request) + ":" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"), _ => new FixedWindowRateLimiterOptions
		{
			PermitLimit = Category(context.Request) == "confirmation" ? 30 : Category(context.Request) == "recovery" ? 5 : 8,
			Window = TimeSpan.FromMinutes(1), QueueLimit = 0
		}));
	options.AddPolicy("account-deletion", context => RateLimitPartition.GetFixedWindowLimiter(
		"context:" + (context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
		_ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
	options.AddPolicy("blocks", context => RateLimitPartition.GetFixedWindowLimiter(
		context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
		_ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
	options.AddPolicy("reports", context => RateLimitPartition.GetFixedWindowLimiter(
		context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
		_ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
	options.AddPolicy("moderation", context => RateLimitPartition.GetFixedWindowLimiter(
		context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
		_ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
	options.AddPolicy("administration", context => RateLimitPartition.GetFixedWindowLimiter(
		context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
		_ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
	options.AddPolicy("messages", context => RateLimitPartition.GetFixedWindowLimiter(
		context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
		{
			PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
		}));
	options.AddPolicy("photos", context => RateLimitPartition.GetFixedWindowLimiter(
		context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
		{
			PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
		}));
	options.AddPolicy("billing", context => RateLimitPartition.GetFixedWindowLimiter(
		context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
		_ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
	options.AddPolicy("billing-public", context => RateLimitPartition.GetFixedWindowLimiter(
		context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
		_ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

static string Category(HttpRequest request)
{
		if (HttpMethods.IsGet(request.Method)) return "confirmation";
		if (request.Path.StartsWithSegments("/api/auth/forgotPassword") || request.Path.StartsWithSegments("/api/auth/resetPassword")) return "recovery";
		if (request.Path.StartsWithSegments("/api/passkeys")) return "passkeys";
		return "credentials";
}

var publishedWwwroot = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
IFileProvider clientFileProvider;
if (File.Exists(Path.Combine(publishedWwwroot, "index.html")) && Directory.Exists(Path.Combine(publishedWwwroot, "_framework")))
{
	clientFileProvider = new PhysicalFileProvider(publishedWwwroot);
}
else
{
	var serverConfiguration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Name;
	var clientWwwroot = new[] { serverConfiguration, "Debug", "Release" }
		.OfType<string>()
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.Select(configuration => Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "bin", configuration, "net10.0", "wwwroot")))
		.FirstOrDefault(Directory.Exists);
	if (clientWwwroot is null)
		throw new DirectoryNotFoundException("Blazor client output was not found. Build the solution first.");
	var sourceWwwroot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "wwwroot"));
	if (!Directory.Exists(sourceWwwroot))
		throw new DirectoryNotFoundException($"Blazor source wwwroot was not found: {sourceWwwroot}.");
	clientFileProvider = new CompositeFileProvider(new PhysicalFileProvider(sourceWwwroot), new PhysicalFileProvider(clientWwwroot));
}

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
	var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
	await DatabaseMigrationRunner.MigrateAsync(database);
}

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
	try { await next(context); }
	catch (EmailDeliveryException)
	{
		context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
		context.Response.Headers.CacheControl = "no-store";
		await context.Response.WriteAsJsonAsync(new { error = "Le message n'a pas pu être envoyé. Réessaie avec le bouton de renvoi de l'e-mail." });
	}
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.Use(async (context, next) =>
{
	if (context.Request.Path.StartsWithSegments("/api"))
	{
		context.Response.Headers.CacheControl = "no-store";
		context.Response.Headers["X-Content-Type-Options"] = "nosniff";
	}
	await next(context);
});
app.Use(async (context, next) =>
{
	if (context.Request.Path.StartsWithSegments("/api")
		&& !context.Request.Path.Equals("/api/billing/webhook", StringComparison.OrdinalIgnoreCase)
		&& !HttpMethods.IsGet(context.Request.Method)
		&& !HttpMethods.IsHead(context.Request.Method)
		&& !HttpMethods.IsOptions(context.Request.Method)
		&& !HttpMethods.IsTrace(context.Request.Method))
	{
		try
		{
			await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
		}
		catch (AntiforgeryValidationException)
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			await context.Response.WriteAsJsonAsync(new { error = "La requête a expiré ou son jeton de sécurité est invalide. Recharge la page et réessaie." });
			return;
		}
	}
	await next(context);
});
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = clientFileProvider });
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".pdb"] = "application/octet-stream";
contentTypes.Mappings[".dat"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions { FileProvider = clientFileProvider, ContentTypeProvider = contentTypes });
app.MapAuthenticationApi();
app.MapTwoGetherApi();
app.MapPremiumBillingApi();
app.MapAdministrationApi();
app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = clientFileProvider });

app.Run();

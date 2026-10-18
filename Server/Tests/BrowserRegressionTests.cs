using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MimeKit;
using Xunit;

namespace TwoGether.SecurityTests;

public sealed class BrowserRegressionTests
{
    [Fact]
    public async Task Password_recovery_page_requests_code_and_validates_password_confirmation()
    {
        await using var server = await LocalTestServer.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync(new Uri(server.Address, "/mot-de-passe-oublie").ToString());
        await page.Locator("#recovery-email").WaitForAsync();

        await page.Locator("#recovery-email").FillAsync("unknown@security.invalid");
        await page.GetByRole(AriaRole.Button, new() { Name = "Envoyer un code de récupération" }).ClickAsync();

        var feedback = page.Locator("p[role='status']").Last;
        await feedback.WaitForAsync();
        Assert.Contains(
            "Si un compte confirmé correspond à cette adresse",
            await feedback.InnerTextAsync(),
            StringComparison.Ordinal);
        await page.Locator("#recovery-code").WaitForAsync();

        await page.Locator("#recovery-code").FillAsync("invalid-code");
        await page.Locator("#recovery-new-password").FillAsync("New-password-12345");
        await page.Locator("#recovery-confirm-password").FillAsync("Different-password-12345");
        await page.GetByRole(AriaRole.Button, new() { Name = "Modifier mon mot de passe" }).ClickAsync();

        Assert.Contains(
            "Les deux mots de passe ne correspondent pas.",
            await feedback.InnerTextAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_recovery_flow_uses_email_code_to_change_password()
    {
        const string email = "recovery@security.invalid";
        const string newPassword = "New-security-password-123";
        await using var server = await LocalTestServer.StartAsync();
        using (var registrationClient = CreateTestClient(server.Address, out _))
            await CreateConfirmedAccountAsync(registrationClient, email, server.MailDirectory);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync(new Uri(server.Address, "/mot-de-passe-oublie").ToString());
        await page.Locator("#recovery-email").FillAsync(email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Envoyer un code de récupération" }).ClickAsync();

        var feedback = page.Locator("p[role='status']").Last;
        await feedback.WaitForAsync();
        Assert.Contains("Si un compte confirmé correspond à cette adresse", await feedback.InnerTextAsync(), StringComparison.Ordinal);
        await page.Locator("#recovery-code").WaitForAsync();

        using var recoveryMessage = Directory.GetFiles(server.MailDirectory, "*.eml")
            .Select(path => MimeMessage.Load(path))
            .Single(message => message.Subject == "Récupération de ton compte 2gether"
                && message.To.Mailboxes.Any(mailbox => mailbox.Address == email));
        var codeMatch = Regex.Match(recoveryMessage.TextBody ?? "", @"Ton code de récupération 2gether est\s*:\s*(?<code>\S+)");
        Assert.True(codeMatch.Success, "The recovery email did not contain a reset code.");

        await page.Locator("#recovery-code").FillAsync(codeMatch.Groups["code"].Value);
        await page.Locator("#recovery-new-password").FillAsync(newPassword);
        await page.Locator("#recovery-confirm-password").FillAsync(newPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Modifier mon mot de passe" }).ClickAsync();
        await feedback.WaitForAsync();
        Assert.Contains("Ton mot de passe a été modifié.", await feedback.InnerTextAsync(), StringComparison.Ordinal);

        using var loginClient = CreateTestClient(server.Address, out _);
        using var login = await loginClient.PostAsJsonAsync(
            "/api/auth/login?useCookies=true",
            new { Email = email, Password = newPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Administrator_can_set_and_reload_message_retention_at_both_limits()
    {
        const string email = "admin@security.invalid";
        const string password = "Security-check-12345";
        await using var server = await LocalTestServer.StartAsync(email);
        CookieContainer authenticatedCookies;
        using (var client = CreateTestClient(server.Address, out authenticatedCookies))
        {
            await CreateConfirmedAccountAsync(client, email, server.MailDirectory);
            using var login = await client.PostAsJsonAsync(
                "/api/auth/login?useCookies=true",
                new { Email = email, Password = password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync();
        await context.AddCookiesAsync(authenticatedCookies.GetCookies(server.Address)
            .Cast<System.Net.Cookie>()
            .Select(cookie => new Microsoft.Playwright.Cookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = server.Address.Host,
                Path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path,
                HttpOnly = cookie.HttpOnly,
                Secure = cookie.Secure,
                SameSite = SameSiteAttribute.Lax
            })
            .ToArray());
        var page = await context.NewPageAsync();

        await page.GotoAsync(new Uri(server.Address, "/administration/conservation").ToString());
        var slider = page.Locator("#message-retention-days");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('#message-retention-days') !== null || document.querySelector('[role=alert]') !== null");
        Assert.True(await slider.CountAsync() > 0, await page.Locator("body").InnerTextAsync());
        Assert.Equal("1", await slider.GetAttributeAsync("min"));
        Assert.Equal("150", await slider.GetAttributeAsync("max"));
        Assert.Equal("30", await slider.EvaluateAsync<string>("element => element.value"));
        Assert.Equal("1 mois (30 jours)", await page.Locator("#message-retention-value").InnerTextAsync());

        await slider.FocusAsync();
        await slider.PressAsync("End");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('#message-retention-days')?.value === '150'");
        Assert.Equal("5 mois (150 jours)", await page.Locator("#message-retention-value").InnerTextAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Enregistrer la durée" }).ClickAsync();
        await AssertRetentionSavedAsync(page);

        await page.ReloadAsync();
        await slider.WaitForAsync();
        Assert.Equal("150", await slider.EvaluateAsync<string>("element => element.value"));
        await slider.FocusAsync();
        await slider.PressAsync("Home");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('#message-retention-days')?.value === '1'");
        Assert.Equal("1 jour", await page.Locator("#message-retention-value").InnerTextAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Enregistrer la durée" }).ClickAsync();
        await AssertRetentionSavedAsync(page);

        await page.ReloadAsync();
        await slider.WaitForAsync();
        Assert.Equal("1", await slider.EvaluateAsync<string>("element => element.value"));
    }

    private static async Task AssertRetentionSavedAsync(IPage page)
    {
        var feedback = page.Locator("p[role='status']").Last;
        await feedback.WaitForAsync();
        Assert.Contains("La durée de conservation a été enregistrée.", await feedback.InnerTextAsync(), StringComparison.Ordinal);
    }

    private static async Task CreateConfirmedAccountAsync(HttpClient client, string email, string mailDirectory)
    {
        var registration = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Email = email, Password = "Security-check-12345" });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        registration.Dispose();

        using var mail = Directory.GetFiles(mailDirectory, "*.eml")
            .Select(path => MimeMessage.Load(path))
            .Single(message => message.To.Mailboxes.Any(mailbox => mailbox.Address == email));
        var confirmationLink = new Uri(Regex.Match(mail.TextBody ?? "", @"https?://\S+").Value);
        using var confirmation = await client.GetAsync("/api/auth/confirmEmail" + confirmationLink.Query);
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
    }

    private static HttpClient CreateTestClient(Uri address, out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return new HttpClient(new TestAntiforgeryHandler
        {
            InnerHandler = new HttpClientHandler
            {
                CookieContainer = cookies,
                AllowAutoRedirect = false
            }
        })
        {
            BaseAddress = address
        };
    }

    private sealed class TestAntiforgeryHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get
                && request.Method != HttpMethod.Head
                && request.Method != HttpMethod.Options
                && request.Method != HttpMethod.Trace)
            {
                using var tokenRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri(request.RequestUri!, "/api/security/csrf-token"));
                using var tokenResponse = await base.SendAsync(tokenRequest, cancellationToken);
                tokenResponse.EnsureSuccessStatusCode();
                var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(
                    cancellationToken: cancellationToken);
                request.Headers.TryAddWithoutValidation(
                    "X-CSRF-TOKEN",
                    tokens.GetProperty("requestToken").GetString());
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static string FindServerDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TwoGether.Server.csproj")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Server project not found.");
    }

    private sealed class LocalTestServer : IAsyncDisposable
    {
        private readonly string temporaryDirectory;
        private readonly Process process;

        private LocalTestServer(Uri address, string temporaryDirectory, string mailDirectory, Process process)
        {
            Address = address;
            this.temporaryDirectory = temporaryDirectory;
            MailDirectory = mailDirectory;
            this.process = process;
        }

        public Uri Address { get; }
        public string MailDirectory { get; }

        public static async Task<LocalTestServer> StartAsync(string? administratorEmail = null)
        {
            var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"2gether-browser-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            var mailDirectory = Path.Combine(temporaryDirectory, "mail");
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var address = new Uri($"http://127.0.0.1:{port}");
            var serverDirectory = FindServerDirectory();
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = serverDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(Path.Combine(serverDirectory, "bin", "Release", "net10.0", "TwoGether.Server.dll"));
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_URLS"] = address.ToString();
            start.Environment["TWOGETHER_DB_PATH"] = Path.Combine(temporaryDirectory, "browser-test.db");
            start.Environment["Email__Delivery"] = "PickupDirectory";
            start.Environment["Email__PublicOrigin"] = address.ToString();
            start.Environment["Email__PickupDirectory"] = mailDirectory;
            if (administratorEmail is not null)
                start.Environment["Administration__AdministratorEmails__0"] = administratorEmail;

            try
            {
                var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test server.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var server = new LocalTestServer(address, temporaryDirectory, mailDirectory, process);
                try
                {
                    using var client = new HttpClient { BaseAddress = address };
                    await WaitForServerAsync(client, process);
                    return server;
                }
                catch
                {
                    await server.DisposeAsync();
                    throw;
                }
            }
            catch
            {
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync();
                }
            }
            finally
            {
                process.Dispose();
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, true);
            }
        }
    }

    private static async Task WaitForServerAsync(HttpClient client, Process server)
    {
        HttpRequestException? lastError = null;
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (server.HasExited)
                throw new InvalidOperationException($"The test server exited with code {server.ExitCode} before becoming healthy.");

            try
            {
                using var response = await client.GetAsync("/api/health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException error)
            {
                lastError = error;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The test server did not become healthy.", lastError);
    }
}

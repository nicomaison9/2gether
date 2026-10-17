using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit;

namespace TwoGether.SecurityTests;

public sealed class BrowserRegressionTests
{
    [Fact]
    public async Task Password_recovery_page_requests_code_and_validates_password_confirmation()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"2gether-browser-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
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
        start.Environment["Email__PickupDirectory"] = Path.Combine(temporaryDirectory, "mail");

        Process? server = null;
        try
        {
            server = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test server.");
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            using var readinessClient = new HttpClient { BaseAddress = address };
            await WaitForServerAsync(readinessClient, server);

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(new Uri(address, "/mot-de-passe-oublie").ToString());
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
        finally
        {
            if (server is not null)
            {
                if (!server.HasExited)
                {
                    server.Kill(true);
                    await server.WaitForExitAsync();
                }
                server.Dispose();
            }

            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, true);
        }
    }

    private static async Task WaitForServerAsync(HttpClient client, Process server)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (server.HasExited)
                throw new InvalidOperationException("The test server exited before becoming healthy.");

            try
            {
                using var response = await client.GetAsync("/api/health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The test server did not become healthy.");
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
}

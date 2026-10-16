using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ImageMagick;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using System.Text.RegularExpressions;
using Xunit;
using TwoGether.Server;

namespace TwoGether.SecurityTests;

public sealed class SecurityTests
{
    [Fact]
    public async Task Existing_database_without_migration_history_is_preserved()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"2gether-legacy-{Guid.NewGuid():N}.db");
        try
        {
            await using (var legacy = new SqliteConnection($"Data Source={databasePath}"))
            {
                await legacy.OpenAsync();
                await using var command = legacy.CreateCommand();
                command.CommandText = "CREATE TABLE AspNetUsers (Id TEXT NOT NULL PRIMARY KEY); INSERT INTO AspNetUsers (Id) VALUES ('existing-user');";
                await command.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var database = new AppDbContext(options))
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => DatabaseMigrationRunner.MigrateAsync(database));
                Assert.Contains("BaselineLegacySqlite.sql", exception.Message);
            }

            await using var verify = new SqliteConnection($"Data Source={databasePath}");
            await verify.OpenAsync();
            await using var verifyCommand = verify.CreateCommand();
            verifyCommand.CommandText = "SELECT (SELECT COUNT(*) FROM AspNetUsers), (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory');";
            await using var reader = await verifyCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(databasePath + suffix);
        }
    }

    [Fact]
    public async Task Legacy_database_baseline_preserves_existing_accounts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"2gether-baseline-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using var database = new AppDbContext(options);
            await database.Database.EnsureCreatedAsync();
            await database.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AspNetUsers"
                    ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ('preserved-user', 0, 0, 0, 1, 0);
                """);
            var baseline = await File.ReadAllTextAsync(
                Path.Combine(FindServerDirectory(), "Scripts", "BaselineLegacySqlite.sql"));
            await database.Database.ExecuteSqlRawAsync(baseline);

            await DatabaseMigrationRunner.MigrateAsync(database);

            Assert.Equal("preserved-user", await database.Users.Select(user => user.Id).SingleAsync());
            Assert.Empty(await database.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(databasePath + suffix);
        }
    }

    [Fact]
    public async Task Photos_location_and_rate_limits_enforce_security_boundaries()
    {
        var serverDirectory = FindServerDirectory();
        var databasePath = Path.Combine(Path.GetTempPath(), $"2gether-security-{Guid.NewGuid():N}.db");
        var mailDirectory = databasePath + ".mail";
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var address = new Uri($"http://127.0.0.1:{port}");
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
        start.Environment["TWOGETHER_DB_PATH"] = databasePath;
        start.Environment["Email__Delivery"] = "PickupDirectory";
        start.Environment["Email__PublicOrigin"] = address.ToString();
        start.Environment["Email__PickupDirectory"] = mailDirectory;
        using var server = Process.Start(start)!;
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        try
        {
            using var anonymous = CreateClient(address);
            await WaitForServerAsync(anonymous, server);
            foreach (var asset in Directory.GetFiles(Path.Combine(serverDirectory, "..", "bin", "Release", "net10.0", "wwwroot", "_framework"), "icudt*.dat"))
                Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/_framework/{Path.GetFileName(asset)}")).StatusCode);
            using var owner = CreateClient(address);
            using var requester = CreateClient(address);
            var ownerId = await CreateAccountAsync(owner, "owner@security.invalid", mailDirectory);
            var requesterId = await CreateAccountAsync(requester, "requester@security.invalid", mailDirectory);

            var mailCount = Directory.GetFiles(mailDirectory, "*.eml").Length;
            Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/resendConfirmationEmail", new { Email = "unknown@security.invalid" })).StatusCode);
            Assert.Equal(mailCount, Directory.GetFiles(mailDirectory, "*.eml").Length);

            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/conversations")).StatusCode);
            await SaveLocationAsync(owner, 48.85661, 2.35222);
            await SaveLocationAsync(requester, 48.85622, 2.35421);
            var before = await requester.GetFromJsonAsync<JsonElement>($"/api/profiles/{ownerId}");
            Assert.Equal(48.855, before.GetProperty("mapLatitude").GetDouble());
            Assert.Equal(2.35, before.GetProperty("mapLongitude").GetDouble());
            Assert.Equal(0.4, before.GetProperty("distanceKm").GetDouble());
            await SaveLocationAsync(owner, 48.85691, 2.35231);
            var after = await requester.GetFromJsonAsync<JsonElement>($"/api/profiles/{ownerId}");
            Assert.Equal(before.GetProperty("mapLatitude").GetDouble(), after.GetProperty("mapLatitude").GetDouble());
            Assert.Equal(before.GetProperty("mapLongitude").GetDouble(), after.GetProperty("mapLongitude").GetDouble());
            Assert.Equal(before.GetProperty("distanceKm").GetDouble(), after.GetProperty("distanceKm").GetDouble());
            var nearby = await requester.GetFromJsonAsync<JsonElement>("/api/profiles?radiusKm=1");
            Assert.Contains(nearby.EnumerateArray(), profile => profile.GetProperty("userId").GetString() == ownerId);
            await SaveLocationAsync(owner, 48.88, 2.39);
            var outsideNeighborhood = await requester.GetFromJsonAsync<JsonElement>("/api/profiles?radiusKm=1");
            Assert.DoesNotContain(outsideNeighborhood.EnumerateArray(), profile => profile.GetProperty("userId").GetString() == ownerId);
            var widerArea = await requester.GetFromJsonAsync<JsonElement>("/api/profiles?radiusKm=5");
            Assert.Contains(widerArea.EnumerateArray(), profile => profile.GetProperty("userId").GetString() == ownerId);

            using var original = new MagickImage(MagickColors.Red, 32, 32);
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Artist, "Private metadata");
            exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(48), new Rational(51), new Rational(24) });
            original.SetProfile(exif);
            var imageBytes = original.ToByteArray(MagickFormat.Jpeg);
            string firstAvatar = "";
            for (var index = 0; index < 3; index++)
            {
                using var upload = await UploadAsync(owner, imageBytes, "public", true);
                Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
                var photo = await upload.Content.ReadFromJsonAsync<JsonElement>();
                if (index == 0) firstAvatar = photo.GetProperty("url").GetString()!;
                using var download = await owner.GetAsync(photo.GetProperty("url").GetString());
                Assert.True(download.Headers.CacheControl?.NoStore);
                using var sanitized = new MagickImage(await download.Content.ReadAsByteArrayAsync());
                Assert.Null(sanitized.GetExifProfile());
            }
            Assert.Single((await owner.GetFromJsonAsync<JsonElement>("/api/me/photos")).EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(firstAvatar)).StatusCode);

            for (var index = 0; index < 3; index++)
                Assert.Equal(HttpStatusCode.Created, (await UploadAsync(owner, imageBytes, "public", false)).StatusCode);
            var simultaneous = await Task.WhenAll(UploadAsync(owner, imageBytes, "public", false), UploadAsync(owner, imageBytes, "public", false));
            Assert.Single(simultaneous, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(simultaneous, response => response.StatusCode == HttpStatusCode.BadRequest);
            foreach (var response in simultaneous) response.Dispose();
            Assert.Equal(5, (await owner.GetFromJsonAsync<JsonElement>("/api/me/photos")).GetArrayLength());
            Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, new byte[] { 255, 216, 255, 0 }, "public", true)).StatusCode);
            using var oversized = new MagickImage(MagickColors.Red, 13000, 2);
            Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, oversized.ToByteArray(MagickFormat.Png), "public", true)).StatusCode);

            using var privateUpload = await UploadAsync(owner, imageBytes, "private", false);
            Assert.Equal(HttpStatusCode.Created, privateUpload.StatusCode);
            var privatePhoto = await privateUpload.Content.ReadFromJsonAsync<JsonElement>();
            var privateUrl = privatePhoto.GetProperty("url").GetString();
            Assert.Equal(HttpStatusCode.NotFound, (await requester.GetAsync(privateUrl)).StatusCode);
            using var request = await requester.PostAsync($"/api/profiles/{ownerId}/photo-requests", null);
            Assert.Equal(HttpStatusCode.Created, request.StatusCode);
            var requestId = (await request.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.NotFound, (await requester.PostAsync($"/api/me/photo-requests/{requestId}/approve", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/me/photo-requests/{requestId}/approve", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await requester.GetAsync(privateUrl)).StatusCode);

            for (var index = 0; index < 30; index++)
                Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/messages", new { RecipientId = requesterId, Text = "Security test" })).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.PostAsJsonAsync("/api/messages", new { RecipientId = requesterId, Text = "Over limit" })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await requester.PostAsJsonAsync("/api/messages", new { RecipientId = ownerId, Text = "Independent quota" })).StatusCode);
            Assert.Empty((await anonymous.GetAsync($"/api/messages/{ownerId}")).Headers.Location?.ToString() ?? "");

            var limited = false;
            for (var index = 0; index < 9; index++)
            {
                using var login = await anonymous.PostAsJsonAsync("/api/auth/login?useCookies=true", new { Email = "owner@security.invalid", Password = "Security-check-12345" });
                limited |= login.StatusCode == HttpStatusCode.TooManyRequests;
            }
            Assert.True(limited);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/session")).StatusCode);
            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version()";
            Assert.True(Version.Parse((string)(await command.ExecuteScalarAsync())!) >= new Version(3, 50, 2));

            var deletionOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var seed = new AppDbContext(deletionOptions))
            {
                seed.PasskeyCredentials.Add(new PasskeyCredential
                {
                    CredentialId = "deletion-test-credential",
                    UserId = ownerId,
                    PublicKey = [1],
                    UserHandle = [1]
                });
                await seed.SaveChangesAsync();
            }

            using (var invalidDeletion = await owner.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/me/account")
            {
                Content = JsonContent.Create(new { Password = "Incorrect-password-123" })
            }))
                Assert.Equal(HttpStatusCode.Unauthorized, invalidDeletion.StatusCode);
            Assert.True((await owner.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("isAuthenticated").GetBoolean());

            using (var deletion = await owner.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/me/account")
            {
                Content = JsonContent.Create(new { Password = "Security-check-12345" })
            }))
                Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
            Assert.False((await owner.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("isAuthenticated").GetBoolean());

            await using var verifyDeletion = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await verifyDeletion.OpenAsync();
            await using var verifyCommand = verifyDeletion.CreateCommand();
            verifyCommand.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM "AspNetUsers" WHERE "Id" = $userId),
                    (SELECT COUNT(*) FROM "UserProfiles" WHERE "UserId" = $userId),
                    (SELECT COUNT(*) FROM "ProfilePhotos" WHERE "OwnerId" = $userId),
                    (SELECT COUNT(*) FROM "PasskeyCredentials" WHERE "UserId" = $userId),
                    (SELECT COUNT(*) FROM "PhotoAccessRequests" WHERE "OwnerId" = $userId OR "RequesterId" = $userId),
                    (SELECT COUNT(*) FROM "DirectMessages" WHERE "SenderId" = $userId OR "RecipientId" = $userId);
                """;
            verifyCommand.Parameters.AddWithValue("$userId", ownerId);
            await using var verifyReader = await verifyCommand.ExecuteReaderAsync();
            Assert.True(await verifyReader.ReadAsync());
            for (var column = 0; column < verifyReader.FieldCount; column++)
                Assert.Equal(0L, verifyReader.GetInt64(column));
        }
        finally
        {
            if (!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(databasePath + suffix);
            if (Directory.Exists(mailDirectory)) Directory.Delete(mailDirectory, true);
        }
    }

    [Fact]
    public async Task Account_recovery_and_passkeys_require_valid_single_use_challenges()
    {
        var serverDirectory = FindServerDirectory();
        var databasePath = Path.Combine(Path.GetTempPath(), $"2gether-account-security-{Guid.NewGuid():N}.db");
        var mailDirectory = databasePath + ".mail";
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var address = new Uri($"http://127.0.0.1:{port}");
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
        start.Environment["TWOGETHER_DB_PATH"] = databasePath;
        start.Environment["Email__Delivery"] = "PickupDirectory";
        start.Environment["Email__PublicOrigin"] = address.ToString();
        start.Environment["Email__PickupDirectory"] = mailDirectory;
        using var server = Process.Start(start)!;
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        try
        {
            using var anonymous = CreateClient(address);
            using var owner = CreateClient(address);
            await WaitForServerAsync(anonymous, server);
            using (var withoutCsrf = new HttpClient { BaseAddress = address })
            using (var rejectedMutation = await withoutCsrf.PostAsync("/api/passkeys/login/options", null))
                Assert.Equal(HttpStatusCode.BadRequest, rejectedMutation.StatusCode);
            await CreateAccountAsync(owner, "recovery@security.invalid", mailDirectory);

            var mailCount = Directory.GetFiles(mailDirectory, "*.eml").Length;
            using var unknownRecovery = await anonymous.PostAsJsonAsync("/api/auth/forgotPassword", new { Email = "unknown@security.invalid" });
            Assert.Equal(HttpStatusCode.OK, unknownRecovery.StatusCode);
            Assert.Equal(mailCount, Directory.GetFiles(mailDirectory, "*.eml").Length);

            using var recovery = await anonymous.PostAsJsonAsync("/api/auth/forgotPassword", new { Email = "recovery@security.invalid" });
            Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
            using var mail = Directory.GetFiles(mailDirectory, "*.eml")
                .Select(path => MimeMessage.Load(path))
                .Single(message => message.Subject == "Récupération de ton compte 2gether"
                    && message.To.Mailboxes.Any(mailbox => mailbox.Address == "recovery@security.invalid"));
            var codeMatch = Regex.Match(mail.TextBody ?? "", @"Ton code de récupération 2gether est\s*:\s*(?<code>\S+)");
            Assert.True(codeMatch.Success, "Recovery email did not contain a reset code.");
            var resetCode = codeMatch.Groups["code"].Value;

            using var invalidReset = await anonymous.PostAsJsonAsync("/api/auth/resetPassword", new
            {
                Email = "recovery@security.invalid",
                ResetCode = "invalid-code",
                NewPassword = "New-security-password-123"
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalidReset.StatusCode);

            using var validReset = await anonymous.PostAsJsonAsync("/api/auth/resetPassword", new
            {
                Email = "recovery@security.invalid",
                ResetCode = resetCode,
                NewPassword = "New-security-password-123"
            });
            Assert.True(validReset.IsSuccessStatusCode);
            using var replayedReset = await anonymous.PostAsJsonAsync("/api/auth/resetPassword", new
            {
                Email = "recovery@security.invalid",
                ResetCode = resetCode,
                NewPassword = "Another-password-98765"
            });
            Assert.Equal(HttpStatusCode.BadRequest, replayedReset.StatusCode);

            using var oldPasswordLogin = await anonymous.PostAsJsonAsync("/api/auth/login?useCookies=true", new
            {
                Email = "recovery@security.invalid",
                Password = "Security-check-12345"
            });
            Assert.Equal(HttpStatusCode.Unauthorized, oldPasswordLogin.StatusCode);
            using var newPasswordLogin = await anonymous.PostAsJsonAsync("/api/auth/login?useCookies=true", new
            {
                Email = "recovery@security.invalid",
                Password = "New-security-password-123"
            });
            Assert.Equal(HttpStatusCode.OK, newPasswordLogin.StatusCode);

            using var unauthenticated = CreateClient(address);
            using var unauthenticatedRegistration = await unauthenticated.PostAsync("/api/passkeys/registration/options", null);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedRegistration.StatusCode);
            using var registrationOptionsResponse = await owner.PostAsync("/api/passkeys/registration/options", null);
            Assert.Equal(HttpStatusCode.OK, registrationOptionsResponse.StatusCode);
            var registrationEnvelope = await registrationOptionsResponse.Content.ReadFromJsonAsync<JsonElement>();
            var registrationOptions = registrationEnvelope.GetProperty("options");
            Assert.Equal("required", registrationOptions.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString());
            Assert.Equal("required", registrationOptions.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());

            var invalidCredential = new
            {
                id = "ZmFrZQ",
                rawId = "ZmFrZQ",
                type = "public-key",
                response = new { clientDataJSON = "", attestationObject = "", transports = Array.Empty<string>() }
            };
            var registrationBody = new { operationId = registrationEnvelope.GetProperty("operationId").GetString(), credential = invalidCredential };
            using var invalidRegistration = await owner.PostAsJsonAsync("/api/passkeys/registration/complete", registrationBody);
            Assert.Equal(HttpStatusCode.BadRequest, invalidRegistration.StatusCode);
            using var replayedRegistration = await owner.PostAsJsonAsync("/api/passkeys/registration/complete", registrationBody);
            Assert.Equal(HttpStatusCode.BadRequest, replayedRegistration.StatusCode);

            using var assertionOptionsResponse = await unauthenticated.PostAsync("/api/passkeys/login/options", null);
            Assert.Equal(HttpStatusCode.OK, assertionOptionsResponse.StatusCode);
            var assertionEnvelope = await assertionOptionsResponse.Content.ReadFromJsonAsync<JsonElement>();
            var assertionOptions = assertionEnvelope.GetProperty("options");
            Assert.Equal("required", assertionOptions.GetProperty("userVerification").GetString());
            Assert.Equal(0, assertionOptions.GetProperty("allowCredentials").GetArrayLength());
            var invalidAssertionCredential = new
            {
                id = "ZmFrZQ",
                rawId = "ZmFrZQ",
                type = "public-key",
                response = new { clientDataJSON = "", authenticatorData = "", signature = "", userHandle = (string?)null }
            };
            using var invalidAssertion = await unauthenticated.PostAsJsonAsync("/api/passkeys/login/complete", new
            {
                operationId = "not-a-live-challenge",
                credential = invalidAssertionCredential
            });
            Assert.Equal(HttpStatusCode.Unauthorized, invalidAssertion.StatusCode);
        }
        finally
        {
            if (!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(databasePath + suffix);
            if (Directory.Exists(mailDirectory)) Directory.Delete(mailDirectory, true);
        }
    }

    private static HttpClient CreateClient(Uri address)
    {
        var handler = new TestAntiforgeryHandler
        {
            InnerHandler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                AllowAutoRedirect = false
            }
        };
        return new HttpClient(handler) { BaseAddress = address };
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

    private static async Task<string> CreateAccountAsync(HttpClient client, string email, string mailDirectory)
    {
        var credentials = new { Email = email, Password = "Security-check-12345" };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/register", credentials)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login?useCookies=true", credentials)).StatusCode);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/conversations")).StatusCode);
        var mail = Directory.GetFiles(mailDirectory, "*.eml").Select(path => MimeMessage.Load(path)).Single(message => message.To.Mailboxes.Any(mailbox => mailbox.Address == email));
        using (mail)
        {
            var link = new Uri(Regex.Match(mail.TextBody ?? throw new InvalidDataException("Missing confirmation email body."), @"https?://\S+").Value);
            Assert.Equal(client.BaseAddress!.Authority, link.Authority);
            Assert.Equal("/confirm-email", link.AbsolutePath);
            var tamperedQuery = Regex.Replace(link.Query, @"([?&]code=)[^&]*", "${1}invalid");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/confirmEmail" + tamperedQuery)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/confirmEmail" + link.Query)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login?useCookies=true", credentials)).StatusCode);
        var profile = new
        {
            DisplayName = "Security test", Age = 25, Gender = "", SexualOrientation = "", Biography = "", City = "Paris",
            Occupation = "", Education = "", Languages = "", Lifestyle = "", LookingFor = "", Intent = "",
            Interests = Array.Empty<string>(), AffinityReasonIds = Array.Empty<string>(), OnboardingComplete = true
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/me/profile", profile)).StatusCode);
        return (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("userId").GetString()!;
    }

    private static async Task SaveLocationAsync(HttpClient client, double latitude, double longitude) =>
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/me/location", new { Latitude = latitude, Longitude = longitude, AccuracyMeters = 5 })).StatusCode);

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] data, string visibility, bool isAvatar)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(data), "file", "image.jpg");
        return await client.PostAsync($"/api/me/photos?visibility={visibility}&isAvatar={isAvatar}", form);
    }

    private static string FindServerDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TwoGether.Server.csproj"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Server project not found.");
    }

    private static async Task WaitForServerAsync(HttpClient client, Process server)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Assert.False(server.HasExited, "Test server stopped before startup.");
            try { if ((await client.GetAsync("/api/health")).IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Test server did not start.");
    }
}
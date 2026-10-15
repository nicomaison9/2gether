using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace TwoGether.Server;

public static class AuthenticationApi
{
	internal const int ChallengeLifetimeMinutes = 5;

	public static void MapAuthenticationApi(this WebApplication app)
	{
		app.MapGroup("/api/auth").RequireRateLimiting("auth").MapIdentityApi<AppUser>();
		app.MapPost("/api/auth/logout", async (SignInManager<AppUser> signInManager) =>
		{
			await signInManager.SignOutAsync();
			return Results.NoContent();
		}).RequireAuthorization(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

		var passkeys = app.MapGroup("/api/passkeys").RequireRateLimiting("auth");
		passkeys.MapGet("/", ListAsync).RequireAuthorization();
		passkeys.MapPost("/registration/options", RegistrationOptionsAsync).RequireAuthorization();
		passkeys.MapPost("/registration/complete", RegisterAsync).RequireAuthorization();
		passkeys.MapDelete("/{credentialId}", DeleteAsync).RequireAuthorization();
		passkeys.MapPost("/login/options", LoginOptionsAsync).AllowAnonymous();
		passkeys.MapPost("/login/complete", LoginAsync).AllowAnonymous();
	}

	private static async Task<IResult> ListAsync(ClaimsPrincipal principal, AppDbContext db, UserManager<AppUser> users)
	{
		var user = await users.GetUserAsync(principal);
		if (user is null || !user.EmailConfirmed) return Results.Unauthorized();
		var credentials = await db.PasskeyCredentials.AsNoTracking()
			.Where(credential => credential.UserId == user.Id)
			.OrderBy(credential => credential.CreatedAt)
			.Select(credential => new PasskeySummary(
				credential.CredentialId,
				credential.CreatedAt,
				credential.LastUsedAt))
			.ToListAsync();
		return Results.Ok(credentials);
	}

	private static async Task<IResult> RegistrationOptionsAsync(
		HttpContext context,
		ClaimsPrincipal principal,
		AppDbContext db,
		UserManager<AppUser> users,
		IFido2 fido2,
		PasskeyChallengeStore challenges)
	{
		var user = await users.GetUserAsync(principal);
		if (user is null || !user.EmailConfirmed) return Results.Unauthorized();

		var credentials = await db.PasskeyCredentials.AsNoTracking()
			.Where(credential => credential.UserId == user.Id)
			.Select(credential => credential.CredentialId)
			.ToListAsync(context.RequestAborted);
		var options = fido2.RequestNewCredential(new RequestNewCredentialParams
		{
			User = new Fido2User
			{
				Id = Encoding.UTF8.GetBytes(user.Id),
				Name = user.Email ?? user.UserName ?? user.Id,
				DisplayName = user.UserName ?? user.Email ?? user.Id
			},
			ExcludeCredentials = credentials.Select(id => new PublicKeyCredentialDescriptor(WebEncoders.Base64UrlDecode(id))).ToList(),
			AuthenticatorSelection = new AuthenticatorSelection
			{
				ResidentKey = ResidentKeyRequirement.Required,
				UserVerification = UserVerificationRequirement.Required
			},
			AttestationPreference = AttestationConveyancePreference.None
		});
		if (!challenges.TryStore("registration", user.Id, options.ToJson(), out var operationId))
			return Results.Problem("Trop de demandes d’authentification sont en cours. Réessaie dans un instant.", statusCode: StatusCodes.Status503ServiceUnavailable);
		return OptionsResponse(operationId, options.ToJson());
	}

	private static async Task<IResult> RegisterAsync(
		HttpContext context,
		ClaimsPrincipal principal,
		RegisterPasskeyRequest request,
		AppDbContext db,
		UserManager<AppUser> users,
		IFido2 fido2,
		PasskeyChallengeStore challenges)
	{
		var user = await users.GetUserAsync(principal);
		if (user is null || !user.EmailConfirmed) return Results.Unauthorized();
		if (!challenges.TryConsume(request.OperationId, "registration", user.Id, out var optionsJson))
			return Results.BadRequest(new { error = "La demande a expiré ou a déjà été utilisée. Recommence l’enregistrement." });

		try
		{
			var result = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
			{
				AttestationResponse = request.Credential,
				OriginalOptions = CredentialCreateOptions.FromJson(optionsJson),
				IsCredentialIdUniqueToUserCallback = async (parameters, cancellationToken) =>
					!await db.PasskeyCredentials.AnyAsync(
						credential => credential.CredentialId == WebEncoders.Base64UrlEncode(parameters.CredentialId),
						cancellationToken)
			}, context.RequestAborted);

			var credentialId = WebEncoders.Base64UrlEncode(result.Id);
			db.PasskeyCredentials.Add(new PasskeyCredential
			{
				CredentialId = credentialId,
				UserId = user.Id,
				PublicKey = result.PublicKey,
				UserHandle = Encoding.UTF8.GetBytes(user.Id),
				SignatureCounter = result.SignCount,
				CreatedAt = DateTimeOffset.UtcNow
			});
			await db.SaveChangesAsync(context.RequestAborted);
			return Results.Created("/api/passkeys", new PasskeySummary(credentialId, DateTimeOffset.UtcNow, null));
		}
		catch (Fido2VerificationException)
		{
			return Results.BadRequest(new { error = "L’appareil n’a pas pu être vérifié. Recommence l’enregistrement." });
		}
		catch (DbUpdateException)
		{
			return Results.Conflict(new { error = "Cette passkey est déjà associée à un compte." });
		}
	}

	private static async Task<IResult> DeleteAsync(string credentialId, ClaimsPrincipal principal, AppDbContext db, UserManager<AppUser> users)
	{
		var user = await users.GetUserAsync(principal);
		if (user is null || !user.EmailConfirmed) return Results.Unauthorized();
		var credential = await db.PasskeyCredentials.SingleOrDefaultAsync(item => item.CredentialId == credentialId && item.UserId == user.Id);
		if (credential is null) return Results.NotFound();
		db.PasskeyCredentials.Remove(credential);
		await db.SaveChangesAsync();
		return Results.NoContent();
	}

	private static IResult LoginOptionsAsync(IFido2 fido2, PasskeyChallengeStore challenges)
	{
		var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
		{
			AllowedCredentials = [],
			UserVerification = UserVerificationRequirement.Required
		});
		if (!challenges.TryStore("login", null, options.ToJson(), out var operationId))
			return Results.Problem("Trop de demandes d’authentification sont en cours. Réessaie dans un instant.", statusCode: StatusCodes.Status503ServiceUnavailable);
		return OptionsResponse(operationId, options.ToJson());
	}

	private static async Task<IResult> LoginAsync(
		HttpContext context,
		LoginPasskeyRequest request,
		AppDbContext db,
		UserManager<AppUser> users,
		SignInManager<AppUser> signInManager,
		IFido2 fido2,
		PasskeyChallengeStore challenges)
	{
		if (!challenges.TryConsume(request.OperationId, "login", null, out var optionsJson))
			return Results.Unauthorized();
		var credentialId = WebEncoders.Base64UrlEncode(request.Credential.RawId);
		var storedCredential = await db.PasskeyCredentials.SingleOrDefaultAsync(
			credential => credential.CredentialId == credentialId,
			context.RequestAborted);
		if (storedCredential is null) return Results.Unauthorized();

		var options = AssertionOptions.FromJson(optionsJson);
		try
		{
			var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
			{
				AssertionResponse = request.Credential,
				OriginalOptions = options,
				StoredPublicKey = storedCredential.PublicKey,
				StoredSignatureCounter = checked((uint)storedCredential.SignatureCounter),
				IsUserHandleOwnerOfCredentialIdCallback = (parameters, _) => Task.FromResult(
					parameters.CredentialId.SequenceEqual(request.Credential.RawId)
					&& parameters.UserHandle.SequenceEqual(storedCredential.UserHandle))
			}, context.RequestAborted);

			var user = await users.FindByIdAsync(storedCredential.UserId);
			if (user is null || !user.EmailConfirmed || await users.IsLockedOutAsync(user))
				return Results.Unauthorized();
			storedCredential.SignatureCounter = result.SignCount;
			storedCredential.LastUsedAt = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync(context.RequestAborted);
			await signInManager.SignInAsync(user, isPersistent: false, authenticationMethod: "passkey");
			return Results.Ok(new { authenticated = true });
		}
		catch (Fido2VerificationException)
		{
			return Results.Unauthorized();
		}
	}

	private static IResult OptionsResponse(string operationId, string json)
	{
		using var document = JsonDocument.Parse(json);
		return Results.Json(new { operationId, options = document.RootElement.Clone() });
	}

	private sealed record RegisterPasskeyRequest(string OperationId, AuthenticatorAttestationRawResponse Credential);
	private sealed record LoginPasskeyRequest(string OperationId, AuthenticatorAssertionRawResponse Credential);
	private sealed record PasskeySummary(string Id, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);
}

public sealed class PasskeyChallengeStore
{
	private const int MaximumPendingOperations = 4096;
	private readonly object sync = new();
	private readonly Dictionary<string, PendingOperation> operations = new(StringComparer.Ordinal);

	public bool TryStore(string kind, string? userId, string optionsJson, out string operationId)
	{
		lock (sync)
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var key in operations.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
				operations.Remove(key);
			if (operations.Count >= MaximumPendingOperations)
			{
				operationId = "";
				return false;
			}
			operationId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
			operations.Add(operationId, new PendingOperation(kind, userId, optionsJson, now.AddMinutes(AuthenticationApi.ChallengeLifetimeMinutes)));
			return true;
		}
	}

	public bool TryConsume(string operationId, string kind, string? userId, out string optionsJson)
	{
		lock (sync)
		{
			optionsJson = "";
			if (!operations.TryGetValue(operationId, out var operation)
				|| operation.Kind != kind
				|| operation.UserId != userId)
				return false;
			operations.Remove(operationId);
			if (operation.ExpiresAt <= DateTimeOffset.UtcNow) return false;
			optionsJson = operation.OptionsJson;
			return true;
		}
	}

	private sealed record PendingOperation(string Kind, string? UserId, string OptionsJson, DateTimeOffset ExpiresAt);
}

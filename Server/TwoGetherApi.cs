using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ImageMagick;

namespace TwoGether.Server;

public static class TwoGetherApi
{
	private const long MaxPhotoBytes = 5 * 1024 * 1024;
	private const int MaxPhotosPerLibrary = 4;

	public static void MapTwoGetherApi(this WebApplication app)
	{
		app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
		app.MapGet("/api/registration-options", (EmailDeliveryOptions options) => Results.Ok(new { localEmailDelivery = options.Delivery == "PickupDirectory" }));
		app.MapGet("/api/session", async (ClaimsPrincipal principal, AppDbContext db, UserManager<AppUser> users) =>
		{
			if (principal.Identity?.IsAuthenticated != true) return Results.Ok(new { isAuthenticated = false, email = "" });
			var userId = GetUserId(principal);
			var user = await users.FindByIdAsync(userId);
			if (user is null || !user.EmailConfirmed) return Results.Ok(new { isAuthenticated = false, email = "" });
			var onboardingComplete = await db.UserProfiles.Where(profile => profile.UserId == userId).Select(profile => profile.OnboardingComplete).SingleOrDefaultAsync();
			return Results.Ok(new { isAuthenticated = true, userId, email = user?.Email ?? "", onboardingComplete });
		}).AllowAnonymous();

		var me = app.MapGroup("/api/me").RequireAuthorization();
		me.MapGet("/profile", GetOwnProfileAsync);
		me.MapPut("/profile", SaveOwnProfileAsync);
		me.MapPost("/location", SaveLocationAsync);
		me.MapDelete("/location", ClearLocationAsync);
		me.MapGet("/photos", GetOwnPhotosAsync);
		me.MapPost("/photos", UploadPhotoAsync).DisableAntiforgery().RequireRateLimiting("photos");
		me.MapDelete("/photos/{photoId:guid}", DeleteOwnPhotoAsync);
		me.MapGet("/photo-requests", GetPhotoRequestsAsync);
		me.MapPost("/photo-requests/{requestId:guid}/approve", ApprovePhotoRequestAsync);
		me.MapPost("/photo-requests/{requestId:guid}/deny", DenyPhotoRequestAsync);

		app.MapGet("/api/profiles", GetNearbyProfilesAsync).RequireAuthorization();
		app.MapGet("/api/profiles/{profileId}", GetPublicProfileAsync).RequireAuthorization();
		app.MapGet("/api/profiles/{profileId}/photos", GetVisiblePhotosAsync).RequireAuthorization();
		app.MapPost("/api/profiles/{profileId}/photo-requests", CreatePhotoRequestAsync).RequireAuthorization();
		app.MapGet("/api/photos/{photoId:guid}", GetPhotoDataAsync).RequireAuthorization();
		app.MapGet("/api/messages/{otherUserId}", GetConversationAsync).RequireAuthorization();
		app.MapGet("/api/conversations", GetConversationsAsync).RequireAuthorization();
		app.MapPost("/api/messages", SendMessageAsync).RequireAuthorization().RequireRateLimiting("messages");
	}

	private static async Task<IResult> GetOwnProfileAsync(ClaimsPrincipal principal, AppDbContext db, UserManager<AppUser> users)
	{
		var userId = GetUserId(principal);
		var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId);
		var user = await users.FindByIdAsync(userId);
		return profile is null || user is null ? Results.NotFound() : Results.Ok(ToOwnProfile(profile, user.Email ?? ""));
	}

	private static async Task<IResult> SaveOwnProfileAsync(ClaimsPrincipal principal, AppDbContext db, ProfileUpsertRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 32)
			return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.DisplayName)] = ["Le prénom est obligatoire et limité à 32 caractères."] });
		if (request.Age is < 18 or > 120)
			return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Age)] = ["L'âge doit être compris entre 18 et 120 ans."] });
		if (request.Interests.Count > 10)
			return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Interests)] = ["Dix passions maximum sont autorisées."] });

		var userId = GetUserId(principal);
		var profile = await db.UserProfiles.FindAsync(userId);
		if (profile is null)
		{
			profile = new UserProfile { UserId = userId };
			db.UserProfiles.Add(profile);
		}

		profile.DisplayName = request.DisplayName.Trim();
		profile.Age = request.Age;
		profile.Gender = Limit(request.Gender, 80);
		profile.SexualOrientation = Limit(request.SexualOrientation, 80);
		profile.Biography = Limit(request.Biography, 600);
		profile.City = Limit(request.City, 80);
		profile.Occupation = Limit(request.Occupation, 80);
		profile.Education = Limit(request.Education, 100);
		profile.Languages = Limit(request.Languages, 100);
		profile.Lifestyle = Limit(request.Lifestyle, 100);
		profile.LookingFor = Limit(request.LookingFor, 200);
		profile.Intent = Limit(request.Intent, 80);
		profile.InterestsJson = JsonSerializer.Serialize(request.Interests.Distinct(StringComparer.OrdinalIgnoreCase).Take(10));
		profile.AffinityReasonIdsJson = JsonSerializer.Serialize(request.AffinityReasonIds.Distinct(StringComparer.Ordinal).Take(100));
		profile.OnboardingComplete = request.OnboardingComplete;
		profile.UpdatedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync();
		return Results.Ok(ToOwnProfile(profile, ""));
	}

	private static async Task<IResult> SaveLocationAsync(ClaimsPrincipal principal, AppDbContext db, LocationUpdateRequest request)
	{
		if (!double.IsFinite(request.Latitude) || request.Latitude is < -90 or > 90 || !double.IsFinite(request.Longitude) || request.Longitude is < -180 or > 180 || !double.IsFinite(request.AccuracyMeters) || request.AccuracyMeters is < 0 or > 100000)
			return Results.BadRequest(new { error = "Coordonnées GPS invalides." });

		var profile = await db.UserProfiles.FindAsync(GetUserId(principal));
		if (profile is null) return Results.Conflict(new { error = "Crée d'abord ton profil." });
		profile.Latitude = request.Latitude;
		profile.Longitude = request.Longitude;
		profile.LocationAccuracyMeters = request.AccuracyMeters;
		profile.UpdatedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync();
		return Results.NoContent();
	}

	private static async Task<IResult> ClearLocationAsync(ClaimsPrincipal principal, AppDbContext db)
	{
		var profile = await db.UserProfiles.FindAsync(GetUserId(principal));
		if (profile is null) return Results.NotFound();
		profile.Latitude = null;
		profile.Longitude = null;
		profile.LocationAccuracyMeters = null;
		profile.UpdatedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync();
		return Results.NoContent();
	}

	private static async Task<IResult> GetNearbyProfilesAsync(ClaimsPrincipal principal, AppDbContext db, double? radiusKm = null)
	{
		var radius = radiusKm ?? 25;
		if (!double.IsFinite(radius) || radius is < 1 or > 100) return Results.BadRequest(new { error = "Le rayon doit être compris entre 1 et 100 km." });
		var userId = GetUserId(principal);
		var ownProfile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId);
		if (ownProfile?.Latitude is not double latitude || ownProfile.Longitude is not double longitude) return Results.Ok(Array.Empty<PublicProfileResponse>());

		var profiles = await db.UserProfiles.AsNoTracking().Include(item => item.User).Where(item => item.UserId != userId && item.User.EmailConfirmed && item.IsVisible && item.OnboardingComplete && item.Latitude != null && item.Longitude != null).ToListAsync();
		var results = new List<PublicProfileResponse>();
		foreach (var profile in profiles)
		{
			var distance = PublicDistanceKm(latitude, longitude, profile.Latitude!.Value, profile.Longitude!.Value);
			if (distance > radius) continue;
			var publicCount = await db.ProfilePhotos.CountAsync(photo => photo.OwnerId == profile.UserId && photo.Visibility == PhotoVisibility.Public);
			var privateCount = await db.ProfilePhotos.CountAsync(photo => photo.OwnerId == profile.UserId && photo.Visibility == PhotoVisibility.Private);
			var pendingRequest = await db.PhotoAccessRequests.AnyAsync(request => request.OwnerId == profile.UserId && request.RequesterId == userId && !request.IsGranted);
			var avatarId = await db.ProfilePhotos.Where(photo => photo.OwnerId == profile.UserId && photo.IsAvatar).Select(photo => (Guid?)photo.Id).FirstOrDefaultAsync();
			results.Add(ToPublicProfile(profile, distance, publicCount, privateCount, pendingRequest, avatarId));
		}
		return Results.Ok(results.OrderBy(profile => profile.DistanceKm));
	}

	private static async Task<IResult> GetPublicProfileAsync(string profileId, ClaimsPrincipal principal, AppDbContext db)
	{
		var userId = GetUserId(principal);
		var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == profileId && item.User.EmailConfirmed && item.IsVisible && item.OnboardingComplete);
		if (profile is null) return Results.NotFound();
		var ownProfile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId);
		var distance = ownProfile?.Latitude is double latitude && ownProfile.Longitude is double longitude && profile.Latitude is double profileLatitude && profile.Longitude is double profileLongitude ? PublicDistanceKm(latitude, longitude, profileLatitude, profileLongitude) : 0;
		var publicCount = await db.ProfilePhotos.CountAsync(photo => photo.OwnerId == profile.UserId && photo.Visibility == PhotoVisibility.Public);
		var privateCount = await db.ProfilePhotos.CountAsync(photo => photo.OwnerId == profile.UserId && photo.Visibility == PhotoVisibility.Private);
		var pending = await db.PhotoAccessRequests.AnyAsync(request => request.OwnerId == profile.UserId && request.RequesterId == userId && !request.IsGranted);
		var avatarId = await db.ProfilePhotos.Where(photo => photo.OwnerId == profile.UserId && photo.IsAvatar).Select(photo => (Guid?)photo.Id).FirstOrDefaultAsync();
		return Results.Ok(ToPublicProfile(profile, distance, publicCount, privateCount, pending, avatarId));
	}

	private static async Task<IResult> GetOwnPhotosAsync(ClaimsPrincipal principal, AppDbContext db)
	{
		var ownerId = GetUserId(principal);
		var photos = (await db.ProfilePhotos.AsNoTracking().Where(photo => photo.OwnerId == ownerId).ToListAsync()).OrderByDescending(photo => photo.IsAvatar).ThenBy(photo => photo.CreatedAt);
		return Results.Ok(photos.Select(ToPhotoMetadata));
	}

	private static async Task<IResult> UploadPhotoAsync(HttpContext context, ClaimsPrincipal principal, AppDbContext db, IFormFile file, string visibility, bool isAvatar = false)
	{
		if (file.Length is <= 0 or > MaxPhotoBytes) return Results.BadRequest(new { error = "Une image doit peser entre 1 octet et 5 Mo." });
		if (visibility is not ("public" or "private")) return Results.BadRequest(new { error = "La visibilité doit être public ou private." });
		if (isAvatar) visibility = "public";

		var ownerId = GetUserId(principal);
		var profile = await db.UserProfiles.FindAsync(ownerId);
		if (profile is null) return Results.Conflict(new { error = "Crée d'abord ton profil." });
		await using var memory = new MemoryStream();
		await file.CopyToAsync(memory, context.RequestAborted);
		var data = memory.ToArray();
		var contentType = DetectImageContentType(data);
		if (contentType is null) return Results.BadRequest(new { error = "Formats acceptés : JPEG, PNG ou WebP." });

		try
		{
			var settings = new MagickReadSettings
			{
				FrameCount = 1,
				Format = contentType switch { "image/jpeg" => MagickFormat.Jpeg, "image/png" => MagickFormat.Png, _ => MagickFormat.WebP }
			};
			using var image = new MagickImage();
			image.Ping(data, settings);
			if (image.Width > 12000 || image.Height > 12000 || (long)image.Width * image.Height > 24000000)
				return Results.BadRequest(new { error = "Image trop grande (24 millions de pixels maximum)." });
			image.Read(data, settings);
			image.AutoOrient();
			image.Resize(new MagickGeometry(1600, 1600) { Greater = true });
			image.Strip();
			image.Quality = 85;
			data = image.ToByteArray(MagickFormat.Jpeg);
			contentType = "image/jpeg";
		}
		catch (MagickException)
		{
			return Results.BadRequest(new { error = "Image invalide ou non prise en charge." });
		}

		await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
		var photoVisibility = visibility == "private" ? PhotoVisibility.Private : PhotoVisibility.Public;
		if (!isAvatar && await db.ProfilePhotos.CountAsync(photo => photo.OwnerId == ownerId && photo.Visibility == photoVisibility && !photo.IsAvatar) >= MaxPhotosPerLibrary)
			return Results.BadRequest(new { error = "Cette bibliothèque contient déjà quatre photos." });

		if (isAvatar)
		{
			var existingAvatars = await db.ProfilePhotos.Where(photo => photo.OwnerId == ownerId && photo.IsAvatar).ToListAsync();
			db.ProfilePhotos.RemoveRange(existingAvatars);
		}

		var photo = new ProfilePhoto { OwnerId = ownerId, Visibility = photoVisibility, IsAvatar = isAvatar, ContentType = contentType, FileName = "photo.jpg", Data = data };
		db.ProfilePhotos.Add(photo);
		await db.SaveChangesAsync(context.RequestAborted);
		await transaction.CommitAsync(context.RequestAborted);
		return Results.Created($"/api/photos/{photo.Id}", new PhotoUploadResponse(photo.Id, $"/api/photos/{photo.Id}", photo.Visibility, photo.IsAvatar));
	}

	private static async Task<IResult> DeleteOwnPhotoAsync(Guid photoId, ClaimsPrincipal principal, AppDbContext db)
	{
		var ownerId = GetUserId(principal);
		var photo = await db.ProfilePhotos.SingleOrDefaultAsync(item => item.Id == photoId && item.OwnerId == ownerId);
		if (photo is null) return Results.NotFound();
		db.ProfilePhotos.Remove(photo);
		await db.SaveChangesAsync();
		return Results.NoContent();
	}

	private static async Task<IResult> GetVisiblePhotosAsync(string profileId, ClaimsPrincipal principal, AppDbContext db)
	{
		var viewerId = GetUserId(principal);
		var ownerProfile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(profile => profile.UserId == profileId && profile.User.EmailConfirmed && profile.IsVisible && profile.OnboardingComplete);
		if (ownerProfile is null) return Results.NotFound();
		var canViewPrivate = profileId == viewerId || await HasPrivateAccessAsync(db, profileId, viewerId);
		var photos = (await db.ProfilePhotos.AsNoTracking().Where(photo => photo.OwnerId == profileId && (photo.Visibility == PhotoVisibility.Public || canViewPrivate)).ToListAsync()).OrderByDescending(photo => photo.IsAvatar).ThenBy(photo => photo.CreatedAt);
		return Results.Ok(photos.Select(ToPhotoMetadata));
	}

	private static async Task<IResult> GetPhotoDataAsync(Guid photoId, ClaimsPrincipal principal, AppDbContext db)
	{
		var viewerId = GetUserId(principal);
		var photo = await db.ProfilePhotos.AsNoTracking().SingleOrDefaultAsync(item => item.Id == photoId && item.Owner.EmailConfirmed);
		if (photo is null) return Results.NotFound();
		if (photo.Visibility == PhotoVisibility.Private && photo.OwnerId != viewerId && !await HasPrivateAccessAsync(db, photo.OwnerId, viewerId)) return Results.NotFound();
		return Results.File(photo.Data, photo.ContentType, enableRangeProcessing: true);
	}

	private static async Task<IResult> CreatePhotoRequestAsync(string profileId, ClaimsPrincipal principal, AppDbContext db)
	{
		var requesterId = GetUserId(principal);
		if (profileId == requesterId) return Results.BadRequest(new { error = "Tu n'as pas besoin de demander l'accès à tes propres photos." });
		if (!await db.ProfilePhotos.AnyAsync(photo => photo.OwnerId == profileId && photo.Owner.EmailConfirmed && photo.Visibility == PhotoVisibility.Private)) return Results.NotFound();
		if (await HasPrivateAccessAsync(db, profileId, requesterId)) return Results.Conflict(new { error = "Tu as déjà accès à cet album." });
		var existing = await db.PhotoAccessRequests.FirstOrDefaultAsync(request => request.OwnerId == profileId && request.RequesterId == requesterId && !request.IsGranted);
		if (existing is not null) return Results.Ok(new { existing.Id, existing.IsGranted, existing.CreatedAt });
		var accessRequest = new PhotoAccessRequest { OwnerId = profileId, RequesterId = requesterId };
		db.PhotoAccessRequests.Add(accessRequest);
		await db.SaveChangesAsync();
		return Results.Created($"/api/me/photo-requests/{accessRequest.Id}", new { accessRequest.Id, accessRequest.IsGranted, accessRequest.CreatedAt });
	}

	private static async Task<IResult> GetPhotoRequestsAsync(ClaimsPrincipal principal, AppDbContext db, UserManager<AppUser> users)
	{
		var ownerId = GetUserId(principal);
		var requests = (await db.PhotoAccessRequests.AsNoTracking().Where(request => request.OwnerId == ownerId && !request.IsGranted).ToListAsync()).OrderBy(request => request.CreatedAt);
		var response = new List<PhotoRequestResponse>();
		foreach (var request in requests)
		{
			var user = await users.FindByIdAsync(request.RequesterId);
			var name = await db.UserProfiles.Where(profile => profile.UserId == request.RequesterId).Select(profile => profile.DisplayName).SingleOrDefaultAsync();
			response.Add(new PhotoRequestResponse(request.Id, request.RequesterId, name ?? user?.Email ?? "Membre", request.CreatedAt));
		}
		return Results.Ok(response);
	}

	private static async Task<IResult> ApprovePhotoRequestAsync(Guid requestId, ClaimsPrincipal principal, AppDbContext db)
	{
		var ownerId = GetUserId(principal);
		var request = await db.PhotoAccessRequests.SingleOrDefaultAsync(item => item.Id == requestId && item.OwnerId == ownerId);
		if (request is null) return Results.NotFound();
		request.IsGranted = true;
		request.GrantedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync();
		return Results.NoContent();
	}

	private static async Task<IResult> DenyPhotoRequestAsync(Guid requestId, ClaimsPrincipal principal, AppDbContext db)
	{
		var ownerId = GetUserId(principal);
		var request = await db.PhotoAccessRequests.SingleOrDefaultAsync(item => item.Id == requestId && item.OwnerId == ownerId);
		if (request is null) return Results.NotFound();
		db.PhotoAccessRequests.Remove(request);
		await db.SaveChangesAsync();
		return Results.NoContent();
	}

	private static async Task<IResult> GetConversationAsync(string otherUserId, ClaimsPrincipal principal, AppDbContext db)
	{
		var userId = GetUserId(principal);
		if (otherUserId == userId) return Results.BadRequest();
		var messages = (await db.DirectMessages.AsNoTracking()
			.Where(message => (message.SenderId == userId && message.RecipientId == otherUserId) || (message.SenderId == otherUserId && message.RecipientId == userId))
			.ToListAsync()).OrderBy(message => message.SentAt).TakeLast(500);
		return Results.Ok(messages.Select(message => new DirectMessageResponse(message.Id, message.SenderId, message.RecipientId, message.Text, message.SentAt)));
	}

	private static async Task<IResult> GetConversationsAsync(ClaimsPrincipal principal, AppDbContext db)
	{
		var userId = GetUserId(principal);
		var messages = await db.DirectMessages.AsNoTracking()
			.Where(message => message.SenderId == userId || message.RecipientId == userId)
			.ToListAsync();
		var latestMessages = messages
			.GroupBy(message => message.SenderId == userId ? message.RecipientId : message.SenderId)
			.Select(group => new { OtherUserId = group.Key, LastMessage = group.OrderByDescending(message => message.SentAt).First() })
			.ToList();
		var otherUserIds = latestMessages.Select(item => item.OtherUserId).ToList();
		var otherProfiles = await db.UserProfiles.AsNoTracking().Where(profile => otherUserIds.Contains(profile.UserId)).ToDictionaryAsync(profile => profile.UserId);
		var summaries = latestMessages.Select(item => new ConversationSummaryResponse(
			item.OtherUserId,
			otherProfiles.TryGetValue(item.OtherUserId, out var profile) ? profile.DisplayName : "Membre 2gether",
			otherProfiles.TryGetValue(item.OtherUserId, out profile) ? profile.City : "",
			item.LastMessage.Text,
			item.LastMessage.SentAt)).OrderByDescending(item => item.LastSentAt).ToList();
		return Results.Ok(summaries);
	}

	private static async Task<IResult> SendMessageAsync(ClaimsPrincipal principal, AppDbContext db, DirectMessageRequest request)
	{
		var senderId = GetUserId(principal);
		var text = request.Text?.Trim();
		if (request.RecipientId == senderId || string.IsNullOrWhiteSpace(text) || text.Length > 2000) return Results.BadRequest(new { error = "Message invalide." });
		if (!await db.UserProfiles.AnyAsync(profile => profile.UserId == request.RecipientId && profile.User.EmailConfirmed && profile.IsVisible && profile.OnboardingComplete)) return Results.NotFound();
		var message = new DirectMessage { SenderId = senderId, RecipientId = request.RecipientId, Text = text };
		db.DirectMessages.Add(message);
		await db.SaveChangesAsync();
		return Results.Created($"/api/messages/{request.RecipientId}", new DirectMessageResponse(message.Id, message.SenderId, message.RecipientId, message.Text, message.SentAt));
	}

	private static async Task<bool> HasPrivateAccessAsync(AppDbContext db, string ownerId, string viewerId)
	{
		var grants = await db.PhotoAccessRequests.AsNoTracking().Where(item => item.OwnerId == ownerId && item.RequesterId == viewerId && item.IsGranted).Select(item => item.GrantedAt).ToListAsync();
		return grants.Any(grantedAt => grantedAt is DateTimeOffset date && date > DateTimeOffset.UtcNow.AddDays(-30));
	}

	private static OwnProfileResponse ToOwnProfile(UserProfile profile, string email) => new(
		profile.UserId, email, profile.DisplayName, profile.Age, profile.Gender, profile.SexualOrientation, profile.Biography, profile.City,
		profile.Occupation, profile.Education, profile.Languages, profile.Lifestyle, profile.LookingFor, profile.Intent,
		JsonSerializer.Deserialize<string[]>(profile.InterestsJson) ?? [], JsonSerializer.Deserialize<string[]>(profile.AffinityReasonIdsJson) ?? [],
		profile.Latitude, profile.Longitude, profile.LocationAccuracyMeters, profile.OnboardingComplete);

	private static PublicProfileResponse ToPublicProfile(UserProfile profile, double distance, int publicPhotoCount, int privatePhotoCount, bool hasPendingRequest, Guid? avatarId = null) => new(
		profile.UserId, profile.DisplayName, profile.Age, profile.Gender, profile.SexualOrientation, profile.Biography, profile.City,
		profile.Occupation, profile.Education, profile.Languages, profile.Lifestyle, profile.LookingFor, profile.Intent,
		JsonSerializer.Deserialize<string[]>(profile.InterestsJson) ?? [], distance, PublicCoordinate(profile.Latitude ?? 0), PublicCoordinate(profile.Longitude ?? 0), avatarId is Guid id ? $"/api/photos/{id}" : null, publicPhotoCount, privatePhotoCount, hasPendingRequest);

	private static double PublicCoordinate(double coordinate) => Math.Round(Math.Round(coordinate / 0.005) * 0.005, 6);
	private static double PublicDistanceKm(double lat1, double lon1, double lat2, double lon2) =>
		Math.Round(DistanceKm(PublicCoordinate(lat1), PublicCoordinate(lon1), PublicCoordinate(lat2), PublicCoordinate(lon2)), 1);

	private static PhotoMetadataResponse ToPhotoMetadata(ProfilePhoto photo) => new(photo.Id, $"/api/photos/{photo.Id}", photo.Visibility, photo.IsAvatar, photo.CreatedAt);

	private static string? DetectImageContentType(byte[] data)
	{
		if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff) return "image/jpeg";
		if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
		if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
		return null;
	}

	private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
	{
		const double earthRadiusKm = 6371.0088;
		static double ToRadians(double degrees) => degrees * Math.PI / 180;
		var latDelta = ToRadians(lat2 - lat1);
		var lonDelta = ToRadians(lon2 - lon1);
		var haversine = Math.Pow(Math.Sin(latDelta / 2), 2) + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Pow(Math.Sin(lonDelta / 2), 2);
		return earthRadiusKm * 2 * Math.Asin(Math.Sqrt(Math.Clamp(haversine, 0, 1)));
	}

	private static string GetUserId(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub") ?? throw new InvalidOperationException("Authenticated user id is missing.");
	private static string Limit(string? value, int maxLength) => (value ?? "").Trim() is var trimmed && trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
}
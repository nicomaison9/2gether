using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TwoGether.Server;

public sealed class AppUser : IdentityUser;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
	public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
	public DbSet<ProfilePhoto> ProfilePhotos => Set<ProfilePhoto>();
	public DbSet<PhotoAccessRequest> PhotoAccessRequests => Set<PhotoAccessRequest>();
	public DbSet<DirectMessage> DirectMessages => Set<DirectMessage>();

	protected override void OnModelCreating(ModelBuilder builder)
	{
		base.OnModelCreating(builder);
		builder.Entity<UserProfile>().HasKey(profile => profile.UserId);
		builder.Entity<UserProfile>().HasOne(profile => profile.User).WithOne().HasForeignKey<UserProfile>(profile => profile.UserId).OnDelete(DeleteBehavior.Cascade);
		builder.Entity<ProfilePhoto>().HasIndex(photo => new { photo.OwnerId, photo.Visibility, photo.IsAvatar });
		builder.Entity<PhotoAccessRequest>().HasIndex(request => new { request.OwnerId, request.RequesterId, request.CreatedAt });
		builder.Entity<DirectMessage>().HasIndex(message => new { message.SenderId, message.RecipientId, message.SentAt });
	}
}

public sealed class UserProfile
{
	[MaxLength(450)] public string UserId { get; set; } = "";
	public AppUser User { get; set; } = null!;
	[Required, MaxLength(32)] public string DisplayName { get; set; } = "";
	[Range(18, 120)] public int Age { get; set; }
	[MaxLength(80)] public string Gender { get; set; } = "";
	[MaxLength(80)] public string SexualOrientation { get; set; } = "";
	[MaxLength(600)] public string Biography { get; set; } = "";
	[MaxLength(80)] public string City { get; set; } = "";
	[MaxLength(80)] public string Occupation { get; set; } = "";
	[MaxLength(100)] public string Education { get; set; } = "";
	[MaxLength(100)] public string Languages { get; set; } = "";
	[MaxLength(100)] public string Lifestyle { get; set; } = "";
	[MaxLength(200)] public string LookingFor { get; set; } = "";
	[MaxLength(80)] public string Intent { get; set; } = "";
	public string InterestsJson { get; set; } = "[]";
	public string AffinityReasonIdsJson { get; set; } = "[]";
	public bool OnboardingComplete { get; set; }
	public double? Latitude { get; set; }
	public double? Longitude { get; set; }
	public double? LocationAccuracyMeters { get; set; }
	public bool IsVisible { get; set; } = true;
	public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
	public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PhotoVisibility { Public, Private }

public sealed class ProfilePhoto
{
	public Guid Id { get; set; } = Guid.NewGuid();
	[MaxLength(450)] public string OwnerId { get; set; } = "";
	public AppUser Owner { get; set; } = null!;
	public PhotoVisibility Visibility { get; set; }
	public bool IsAvatar { get; set; }
	[MaxLength(80)] public string ContentType { get; set; } = "image/jpeg";
	[MaxLength(120)] public string FileName { get; set; } = "photo.jpg";
	public byte[] Data { get; set; } = [];
	public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PhotoAccessRequest
{
	public Guid Id { get; set; } = Guid.NewGuid();
	[MaxLength(450)] public string OwnerId { get; set; } = "";
	[MaxLength(450)] public string RequesterId { get; set; } = "";
	public bool IsGranted { get; set; }
	public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
	public DateTimeOffset? GrantedAt { get; set; }
}

public sealed class DirectMessage
{
	public Guid Id { get; set; } = Guid.NewGuid();
	[MaxLength(450)] public string SenderId { get; set; } = "";
	[MaxLength(450)] public string RecipientId { get; set; } = "";
	[Required, MaxLength(2000)] public string Text { get; set; } = "";
	public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record ProfileUpsertRequest(
	string DisplayName,
	int Age,
	string Gender,
	string SexualOrientation,
	string Biography,
	string City,
	string Occupation,
	string Education,
	string Languages,
	string Lifestyle,
	string LookingFor,
	string Intent,
	IReadOnlyList<string> Interests,
	IReadOnlyList<string> AffinityReasonIds,
	bool OnboardingComplete);

public sealed record OwnProfileResponse(
	string UserId,
	string Email,
	string DisplayName,
	int Age,
	string Gender,
	string SexualOrientation,
	string Biography,
	string City,
	string Occupation,
	string Education,
	string Languages,
	string Lifestyle,
	string LookingFor,
	string Intent,
	IReadOnlyList<string> Interests,
	IReadOnlyList<string> AffinityReasonIds,
	double? Latitude,
	double? Longitude,
	double? LocationAccuracyMeters,
	bool OnboardingComplete);

public sealed record PublicProfileResponse(
	string UserId,
	string DisplayName,
	int Age,
	string Gender,
	string SexualOrientation,
	string Biography,
	string City,
	string Occupation,
	string Education,
	string Languages,
	string Lifestyle,
	string LookingFor,
	string Intent,
	IReadOnlyList<string> Interests,
	double DistanceKm,
	double MapLatitude,
	double MapLongitude,
	string? AvatarUrl,
	int PublicPhotoCount,
	int PrivatePhotoCount,
	bool HasPendingPhotoRequest);

public sealed record PhotoMetadataResponse(Guid Id, string Url, PhotoVisibility Visibility, bool IsAvatar, DateTimeOffset CreatedAt);
public sealed record PhotoRequestResponse(Guid Id, string RequesterId, string RequesterName, DateTimeOffset CreatedAt);
public sealed record PhotoUploadResponse(Guid Id, string Url, PhotoVisibility Visibility, bool IsAvatar);
public sealed record LocationUpdateRequest(double Latitude, double Longitude, double AccuracyMeters);
public sealed record DirectMessageRequest(string RecipientId, string Text);
public sealed record DirectMessageResponse(Guid Id, string SenderId, string RecipientId, string Text, DateTimeOffset SentAt);
public sealed record ConversationSummaryResponse(string OtherUserId, string DisplayName, string City, string LastMessage, DateTimeOffset LastSentAt);
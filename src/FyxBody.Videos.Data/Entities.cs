namespace FyxBody.Videos.Data;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Video : Entity
{
    public Guid? OwnerPartnerId { get; set; }
    public Guid? UploadLicenseId { get; set; }
    public string? PartnerReference { get; set; }
    public string? UploadIdempotencyKey { get; set; }
    public string? UploadRequestHash { get; set; }

    public Video() => UpdatedAt = CreatedAt;

    public string ExternalExerciseId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "draft";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class VideoAsset : Entity
{
    public Guid VideoId { get; set; }
    public string Language { get; set; } = "es";
    public string Rendition { get; set; } = "original";
    public string BlobContainer { get; set; } = "";
    public string BlobName { get; set; } = "";
    public string ContentType { get; set; } = "video/mp4";
    public long SizeBytes { get; set; }
    public int DurationMs { get; set; }
    public string Status { get; set; } = "pending";
}

public sealed class Partner : Entity
{
    public Guid? ParentPartnerId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "active";
}

public sealed class PartnerCredential : Entity
{
    public Guid PartnerId { get; set; }
    public string KeyId { get; set; } = "";
    // Encoded one-way hash including algorithm and parameters; never the raw secret.
    public string SecretHash { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class License : Entity
{
    public Guid PartnerId { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "active";
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public long MonthlySessionQuota { get; set; }
    public bool UnlimitedSessions { get; set; }
    public bool AllowSubpartnerManagement { get; set; }
    public bool AllowCatalogueDelegation { get; set; }
    public bool AllowPartnerUploads { get; set; }
    public string UploadQuotaMode { get; set; } = "unlimited";
    public long? UploadQuotaLimit { get; set; }
}

public sealed class LicenseVideo
{
    public Guid? SourceLicenseId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid LicenseId { get; set; }
    public Guid VideoId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlaybackSession : Entity
{
    public Guid PartnerId { get; set; }
    public Guid LicenseId { get; set; }
    public Guid VideoId { get; set; }
    public Guid VideoAssetId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    // SHA-256 of canonical request, used to reject a key reused with another payload.
    public string RequestHash { get; set; } = "";
    public string Status { get; set; } = "issued";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateOnly UsageMonth { get; set; }
}

public sealed class PlaybackEvent : Entity
{
    public Guid PlaybackSessionId { get; set; }
    public string EventKey { get; set; } = "";
    public string Kind { get; set; } = "started";
    public int PositionMs { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class UsageMonthly
{
    public Guid PartnerId { get; set; }
    public Guid LicenseId { get; set; }
    public DateOnly Month { get; set; }
    public long SessionsIssued { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PortalUser : Entity
{
    public PortalUser() => UpdatedAt = CreatedAt;
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "partner";
    public Guid? PartnerId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
    public string PasswordHash { get; set; } = "";
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

namespace FyxBody.Videos.Api.Admin;

public sealed record PartnerInput(string Code, string Name, string Status);
public sealed record CredentialInput(DateTimeOffset? ExpiresAt);
public sealed record LicenseInput(string Name, string Status, DateTimeOffset StartsAt, DateTimeOffset EndsAt, long MonthlySessionQuota, bool? UnlimitedSessions = null,
    bool? AllowSubpartnerManagement = null, bool? AllowCatalogueDelegation = null, bool? AllowPartnerUploads = null,
    string? UploadQuotaMode = null, long? UploadQuotaLimit = null);
public sealed record CatalogueInput(Guid[] VideoIds);
public sealed record VideoInput(string ExternalExerciseId, string Title, string Status);
public sealed record AssetInput(string Language, string Rendition, string BlobContainer, string BlobName, string ContentType, long SizeBytes, int DurationMs, string Status);
public sealed record PartnerView(Guid Id, string Code, string Name, string Status, DateTimeOffset CreatedAt, Guid? ParentPartnerId = null);
public sealed record CredentialView(Guid Id, string KeyId, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt);
public sealed record CreatedCredentialView(CredentialView Credential, string ApiKey);
public sealed record LicenseView(Guid Id, Guid PartnerId, string Name, string Status, DateTimeOffset StartsAt, DateTimeOffset EndsAt, long MonthlySessionQuota, DateTimeOffset CreatedAt, bool UnlimitedSessions = false,
    bool AllowSubpartnerManagement = false, bool AllowCatalogueDelegation = false, bool AllowPartnerUploads = false,
    string UploadQuotaMode = "unlimited", long? UploadQuotaLimit = null);
public sealed record CatalogueView(Guid[] VideoIds);
public sealed record VideoView(Guid Id, string ExternalExerciseId, string Title, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? OwnerPartnerId = null, string? PartnerReference = null);
public sealed record AssetView(Guid Id, Guid VideoId, string Language, string Rendition, string BlobContainer, string BlobName, string ContentType, long SizeBytes, int DurationMs, string Status, DateTimeOffset CreatedAt);
public sealed record AdminPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record AssetPreviewView(Uri Url, DateTimeOffset ExpiresAt);

public sealed record AssetUploadInput(IFormFile File, string Language, string Rendition, int DurationMs);

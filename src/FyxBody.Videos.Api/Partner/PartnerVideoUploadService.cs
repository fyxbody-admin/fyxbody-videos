using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;

namespace FyxBody.Videos.Api.Partner;

public sealed class PartnerVideoUploadService(VideosDbContext db, IVideoAssetStorage storage, IConfiguration configuration,
    TimeProvider clock, VideoUploadSlots slots, PartnerVideoService videos, ILogger<PartnerVideoUploadService> logger)
{
    public async Task<PrivateVideoResponse> UploadAsync(PartnerIdentity identity, Guid licenseId, string? key, HttpRequest request, CancellationToken ct)
    {
        key = AdminValidation.Text(key, 128, "Idempotency-Key");
        await videos.ValidateIdentityAsync(identity, ct);
        var license = await FindLicense(identity, licenseId, ct);
        if (!await db.Videos.AsNoTracking().AnyAsync(video => video.OwnerPartnerId == identity.PartnerId && video.UploadIdempotencyKey == key, ct))
            EnsureUploadsAllowed(license);
        if (!await slots.Gate.WaitAsync(0, ct))
            throw new ApiException(429, "rate_limit_exceeded", "Two video uploads are already in progress; retry later.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            return await UploadCore(identity, licenseId, key, request, timeout.Token);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            throw new ApiException(413, "upload_too_large", "The MP4 file must be no larger than 200 MiB.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(504, "upload_timeout", "The upload exceeded its ten-minute time limit.");
        }
        finally { slots.Gate.Release(); }
    }

    private async Task<PrivateVideoResponse> UploadCore(PartnerIdentity identity, Guid licenseId, string key, HttpRequest request, CancellationToken ct)
    {
        await using var upload = await MultipartVideoUpload.ReadAsync(request, ct, privateUpload: true);
        var title = AdminValidation.Text(upload.Title, 300, "title");
        var reference = Reference(upload.PartnerReference);
        var rendition = AdminValidation.Text(upload.Rendition, 50, "rendition");
        if (!Regex.IsMatch(rendition, @"\A[A-Za-z0-9_-]{1,50}\z"))
            throw new ApiException(400, "invalid_request", "rendition must use letters, numbers, underscore or hyphen.");
        var container = configuration["Storage:UploadContainer"] ?? "";
        if (!(configuration.GetSection("Storage:AllowedContainers").Get<string[]>() ?? ["videos"]).Contains(container, StringComparer.Ordinal))
            throw new ApiException(503, "video_storage_unavailable", "Configure an allowed upload container.");
        var videoId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var language = AdminValidation.Text(upload.Language, 35, "language");
        var input = AdminValidation.Asset(new(language, rendition, container, $"ejercicios/{videoId:D}/{language}/{rendition}/{assetId:D}.mp4",
            "video/mp4", upload.File.Length, upload.DurationMs, "ready"));
        var fileHash = Convert.ToHexString(await SHA256.HashDataAsync(upload.File, ct));
        upload.File.Position = 0;
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { licenseId, title, reference, input.Language, input.Rendition, input.DurationMs, fileHash })));
        var now = clock.GetUtcNow();
        var video = new Video { Id = videoId, OwnerPartnerId = identity.PartnerId, UploadLicenseId = licenseId, PartnerReference = reference,
            UploadIdempotencyKey = key, UploadRequestHash = requestHash, Title = title, ExternalExerciseId = $"private:{videoId:D}",
            Status = "active", CreatedAt = now, UpdatedAt = now };
        var asset = new VideoAsset { Id = assetId, VideoId = videoId, Language = input.Language, Rendition = input.Rendition,
            BlobContainer = container, BlobName = input.BlobName, ContentType = input.ContentType, SizeBytes = input.SizeBytes,
            DurationMs = input.DurationMs, Status = "ready", CreatedAt = now };
        var blobMayExist = false;
        try
        {
            // The licence row serializes capacity reservations. The partner lock serializes idempotency across licences.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Partners.FromSqlInterpolated($"SELECT * FROM videos.partners WHERE id = {identity.PartnerId} FOR UPDATE").AsNoTracking().ToArrayAsync(ct);
            await videos.ValidateIdentityAsync(identity, ct);
            var existing = await db.Videos.AsNoTracking().SingleOrDefaultAsync(value =>
                value.OwnerPartnerId == identity.PartnerId && value.UploadIdempotencyKey == key, ct);
            if (existing is not null)
            {
                if (existing.UploadRequestHash != requestHash) throw new ApiException(409, "idempotency_conflict", "The key was already used with another request.");
                if (existing.Status == "archived") throw NotFound();
                return await Response(existing, ct);
            }
            var license = await FindLicense(identity, licenseId, ct, locked: true);
            EnsureUploadsAllowed(license);
            var usage = await Measure(license, ct);
            if ((license.UploadQuotaMode == "videos" && usage.VideoCount >= license.UploadQuotaLimit) ||
                (license.UploadQuotaMode == "storage" && input.SizeBytes > license.UploadQuotaLimit - usage.StorageBytes))
                throw new ApiException(409, "upload_quota_exceeded", "This licence does not have enough private upload capacity.");

            blobMayExist = true;
            try { await storage.UploadAsync(asset, upload.File, ct); }
            catch (ApiException exception) when (exception.Status == 409)
            {
                blobMayExist = false;
                throw;
            }
            // Expiry can pass while waiting for Blob Storage.
            EnsureUploadsAllowed(license);
            db.Videos.Add(video);
            db.VideoAssets.Add(asset);
            db.LicenseVideos.Add(new LicenseVideo { LicenseId = licenseId, VideoId = videoId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return View(video, [asset]);
        }
        catch when (blobMayExist)
        {
            db.ChangeTracker.Clear();
            using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                if (await db.VideoAssets.AsNoTracking().AnyAsync(value => value.Id == assetId, reconcile.Token))
                    return View(video, [asset]);
                await storage.DeleteAsync(asset, reconcile.Token);
            }
            catch (Exception exception)
            {
                logger.LogError("Could not reconcile private asset {AssetId}; error {ErrorType}.", assetId, exception.GetType().Name);
            }
            throw;
        }
    }

    public async Task<AdminPage<PrivateVideoResponse>> ListAsync(PartnerIdentity identity, Guid licenseId, int? page, int? pageSize, CancellationToken ct)
    {
        await videos.ValidateIdentityAsync(identity, ct);
        await FindLicense(identity, licenseId, ct);
        var (number, size) = AdminValidation.Pagination(page, pageSize);
        var query = db.Videos.AsNoTracking().Where(x => x.UploadLicenseId == licenseId && x.OwnerPartnerId == identity.PartnerId
            && (x.Status != "archived" || db.VideoAssets.Any(asset => asset.VideoId == x.Id && asset.SizeBytes > 0)));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((number - 1) * size).Take(size).ToArrayAsync(ct);
        var ids = rows.Select(x => x.Id).ToArray();
        var assets = await db.VideoAssets.AsNoTracking().Where(x => ids.Contains(x.VideoId) && x.Status == "ready").ToArrayAsync(ct);
        var byVideo = assets.ToLookup(x => x.VideoId);
        return new(rows.Select(x => View(x, byVideo[x.Id].ToArray())).ToArray(), number, size, total);
    }

    public async Task<PrivateVideoUsage> UsageAsync(PartnerIdentity identity, Guid licenseId, CancellationToken ct)
    {
        await videos.ValidateIdentityAsync(identity, ct);
        return await Measure(await FindLicense(identity, licenseId, ct), ct);
    }

    public async Task<PrivateVideoResponse> UpdateAsync(PartnerIdentity identity, Guid videoId, PrivateVideoMetadata metadata, CancellationToken ct)
    {
        await videos.ValidateIdentityAsync(identity, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var video = await LockVideo(identity, videoId, ct);
        if (video.Status == "archived") throw NotFound();
        video.Title = AdminValidation.Text(metadata.Title, 300, "title");
        video.PartnerReference = Reference(metadata.PartnerReference);
        video.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var response = await Response(video, ct);
        await transaction.CommitAsync(ct);
        return response;
    }

    public async Task DeleteAsync(PartnerIdentity identity, Guid videoId, CancellationToken ct)
    {
        await videos.ValidateIdentityAsync(identity, ct);
        // Publish the archive before deleting bytes. A failed cleanup can be retried without exposing a broken video.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var video = await LockVideo(identity, videoId, ct);
            video.Status = "archived";
            video.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        await using var cleanup = await db.Database.BeginTransactionAsync(ct);
        var archived = await LockVideo(identity, videoId, ct);
        var assets = await db.VideoAssets.Where(value => value.VideoId == archived.Id && value.SizeBytes > 0).ToArrayAsync(ct);
        foreach (var asset in assets)
        {
            await storage.DeleteAsync(asset, ct);
            // Retain the asset row for prior playback sessions.
            asset.Status = "disabled";
            asset.SizeBytes = 0;
        }
        await db.SaveChangesAsync(ct);
        await cleanup.CommitAsync(ct);
    }

    private Task<Video> LockVideo(PartnerIdentity identity, Guid videoId, CancellationToken ct) =>
        LockOwnedVideo(identity.PartnerId, videoId, ct);

    private async Task<Video> LockOwnedVideo(Guid partnerId, Guid videoId, CancellationToken ct) =>
        await db.Videos.FromSqlInterpolated($"SELECT * FROM videos.videos WHERE id = {videoId} AND owner_partner_id = {partnerId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw NotFound();

    private async Task<License> FindLicense(PartnerIdentity identity, Guid licenseId, CancellationToken ct, bool locked = false)
    {
        var query = locked
            ? db.Licenses.FromSqlInterpolated($"SELECT * FROM videos.licenses WHERE id = {licenseId} AND partner_id = {identity.PartnerId} FOR UPDATE").AsNoTracking()
            : db.Licenses.AsNoTracking().Where(value => value.Id == licenseId && value.PartnerId == identity.PartnerId);
        return await query.SingleOrDefaultAsync(ct) ?? throw new ApiException(404, "not_found", "Licence was not found.");
    }

    private bool Active(License license)
    {
        var now = clock.GetUtcNow();
        return license.Status == "active" && license.StartsAt <= now && license.EndsAt > now;
    }

    private void EnsureUploadsAllowed(License license)
    {
        if (!Active(license) || !license.AllowPartnerUploads)
            throw new ApiException(403, "partner_uploads_disabled", "This licence does not permit private video uploads.");
    }

    private async Task<PrivateVideoUsage> Measure(License license, CancellationToken ct)
    {
        var videos = db.Videos.AsNoTracking().Where(value => value.UploadLicenseId == license.Id);
        var assets = db.VideoAssets.AsNoTracking().Where(asset => asset.SizeBytes > 0 && videos.Any(video => video.Id == asset.VideoId));
        var bytes = await assets.SumAsync(asset => asset.SizeBytes, ct);
        var count = await videos.CountAsync(video => video.Status != "archived" || assets.Any(asset => asset.VideoId == video.Id), ct);
        return new(license.Id, license.AllowPartnerUploads, license.UploadQuotaMode, license.UploadQuotaLimit, bytes, count, Active(license));
    }

    private async Task<PrivateVideoResponse> Response(Video video, CancellationToken ct) =>
        View(video, await db.VideoAssets.AsNoTracking().Where(value => value.VideoId == video.Id && value.Status == "ready").ToArrayAsync(ct));

    private static PrivateVideoResponse View(Video video, VideoAsset[] assets) =>
        new(video.Id, video.UploadLicenseId!.Value, video.ExternalExerciseId, video.Title, video.PartnerReference,
            assets.Select(value => new AssetResponse(value.Id, value.Language, value.Rendition, value.ContentType, value.SizeBytes, value.DurationMs)).ToArray(), video.Status);

    private static string? Reference(string? value) => string.IsNullOrWhiteSpace(value) ? null : AdminValidation.Text(value, 128, "partnerReference");
    private static ApiException NotFound() => new(404, "not_found", "Private video was not found.");
}

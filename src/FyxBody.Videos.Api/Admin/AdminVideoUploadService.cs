using System.Data;
using System.Text.RegularExpressions;
using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;

namespace FyxBody.Videos.Api.Admin;

public sealed class VideoUploadSlots : IDisposable
{
    public SemaphoreSlim Gate { get; } = new(2, 2);
    public void Dispose() => Gate.Dispose();
}

public sealed class AdminVideoUploadService(VideosDbContext db, IVideoAssetStorage storage, IConfiguration configuration,
    TimeProvider clock, VideoUploadSlots slots, ILogger<AdminVideoUploadService> logger)
{
    public async Task<AssetView> UploadAsync(Guid videoId, HttpRequest request, CancellationToken ct)
    {
        if (!await slots.Gate.WaitAsync(0, ct)) throw new ApiException(429, "rate_limit_exceeded", "Two video uploads are already in progress; retry later.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            var token = timeout.Token;
            var target = await db.Videos.AsNoTracking().SingleOrDefaultAsync(video => video.Id == videoId, token);
            if (target is null)
                throw new ApiException(404, "not_found", "No se ha encontrado video.");
            if (target.OwnerPartnerId.HasValue) throw new ApiException(409, "private_video_upload_required", "Private video assets must use the quota-aware upload route.");
            var container = configuration["Storage:UploadContainer"] ?? "";
            var allowed = configuration.GetSection("Storage:AllowedContainers").Get<string[]>() ?? ["videos"];
            if (!Regex.IsMatch(container, @"\A[a-z0-9]+(?:-[a-z0-9]+)*\z") || container.Length is < 3 or > 63 || !allowed.Contains(container, StringComparer.Ordinal))
                throw new ApiException(503, "video_storage_unavailable", "Configure an allowed Storage:UploadContainer before uploading videos.");
            await using var upload = await MultipartVideoUpload.ReadAsync(request, token);
            var rendition = AdminValidation.Text(upload.Rendition, 50, "rendition");
            if (!Regex.IsMatch(rendition, @"\A[A-Za-z0-9_-]{1,50}\z")) throw new ApiException(400, "invalid_request", "rendition must be a single segment using letters, numbers, underscore or hyphen.");
            var id = Guid.NewGuid();
            var language = AdminValidation.Text(upload.Language, 35, "language");
            var input = AdminValidation.Asset(new(language, rendition, container, $"ejercicios/{videoId:D}/{language}/{rendition}/{id:D}.mp4", "video/mp4", upload.File.Length, upload.DurationMs, "ready"));
            if (await db.VideoAssets.AnyAsync(asset => asset.VideoId == videoId && asset.Language == input.Language && asset.Rendition == input.Rendition, token))
                throw new ApiException(409, "resource_conflict", "An asset already exists for this language and rendition; choose a different rendition.");
            var asset = new VideoAsset { Id = id, VideoId = videoId, Language = input.Language, Rendition = input.Rendition,
                BlobContainer = input.BlobContainer, BlobName = input.BlobName, ContentType = input.ContentType, SizeBytes = input.SizeBytes,
                DurationMs = input.DurationMs, Status = "ready", CreatedAt = clock.GetUtcNow() };
            try { await storage.UploadAsync(asset, upload.File, token); }
            catch (ApiException exception) when (exception.Status == 409) { throw; } // Never delete a pre-existing object.
            catch { await Cleanup(asset); throw; }
            try { await Persist(asset, token); }
            catch
            {
                // A connection failure can occur after the database committed. Delete only after confirming absence.
                db.ChangeTracker.Clear();
                using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                bool? persisted = null;
                try { persisted = await db.VideoAssets.AsNoTracking().AnyAsync(value => value.Id == asset.Id, reconcile.Token); }
                catch (Exception exception) { logger.LogError("Could not reconcile uploaded asset {AssetId}; error {ErrorType}. Operator cleanup may be required.", asset.Id, exception.GetType().Name); }
                if (persisted == true) return View(asset);
                if (persisted == false) await Cleanup(asset);
                throw;
            }
            return View(asset);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        { throw new ApiException(413, "upload_too_large", "The MP4 file must be no larger than 200 MiB."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ApiException(504, "upload_timeout", "The upload exceeded its ten-minute time limit."); }
        finally { slots.Gate.Release(); }
    }

    private async Task Persist(VideoAsset asset, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var video = await db.Videos.FromSqlInterpolated($"SELECT * FROM videos.videos WHERE id = {asset.VideoId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new ApiException(404, "not_found", "No se ha encontrado video.");
        db.VideoAssets.Add(asset); video.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    private async Task Cleanup(VideoAsset asset)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await storage.DeleteAsync(asset, cleanup.Token); }
        catch (Exception exception) { logger.LogError("Could not clean up uploaded asset {AssetId}; error {ErrorType}. Operator cleanup is required.", asset.Id, exception.GetType().Name); }
    }
    private static AssetView View(VideoAsset asset) => new(asset.Id, asset.VideoId, asset.Language, asset.Rendition, asset.BlobContainer,
        asset.BlobName, asset.ContentType, asset.SizeBytes, asset.DurationMs, asset.Status, asset.CreatedAt);
}

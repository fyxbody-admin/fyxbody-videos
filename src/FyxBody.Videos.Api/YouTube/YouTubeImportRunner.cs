using System.Globalization;
using System.Text.RegularExpressions;
using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;

namespace FyxBody.Videos.Api.YouTube;

public sealed class YouTubeImportRunner(VideosDbContext db, IYouTubeMediaProcessor media, IVideoAssetStorage storage,
    IConfiguration configuration, TimeProvider clock, ILogger<YouTubeImportRunner> logger)
{
    public async Task Run(VideoImportJob claimed, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var minutes = configuration.GetValue<int?>("VideoImports:MaxJobMinutes") ?? 20;
        var durationLimit = configuration.GetValue<int?>("VideoImports:MaxDurationSeconds") ?? 600;
        using var progressGate = new SemaphoreSlim(1, 1);
        try
        {
            if (minutes is < 1 or > 20 || durationLimit is < 1 or > 600) throw new ApiException(503, "import_configuration", "Invalid video import limits.");
            if (YouTubeMediaProcessor.ExtendedDuration(configuration, claimed.YoutubeId))
                durationLimit = int.MaxValue / 1000;
            timeout.CancelAfter(TimeSpan.FromMinutes(minutes));
            var ct = timeout.Token;
            await CheckLease(claimed, ct);
            var external = YouTubeImportQueue.EffectiveReference(claimed);
            var existing = await db.Videos.AsNoTracking().SingleOrDefaultAsync(video => video.ExternalExerciseId == external, ct);
            if (existing is not null) await ValidateExisting(claimed, existing, ct);
            var ready = existing is null ? [] : await ReadyHeights(existing.Id, ct);
            var completed = claimed.RequestedHeights.Intersect(ready).OrderDescending().ToArray();
            if (existing is not null) await AttachExisting(claimed, existing, completed, ct);
            var missing = claimed.RequestedHeights.Except(ready).ToArray();
            var stableUpload = OperatingSystem.IsMacOS() && configuration.GetValue<bool>("VideoImports:TakeoutOnly");
            if (stableUpload && existing is not null)
            {
                var recovered = new List<int>();
                foreach (var height in missing)
                {
                    var asset = ImportAsset(existing.Id, height, 0, 0, stableUpload);
                    if (await storage.FindImportAsync(asset, ct) is not { } uploaded) continue;
                    if (uploaded.DurationMs > durationLimit * 1000)
                        throw new ApiException(409, "import_asset_conflict", "The existing import file exceeds the duration limit.");
                    asset.SizeBytes = uploaded.Bytes;
                    asset.DurationMs = uploaded.DurationMs;
                    await PersistAsset(claimed, asset, height, ct);
                    recovered.Add(height);
                }
                missing = missing.Except(recovered).ToArray();
            }
            if (missing.Length == 0) { await Complete(claimed, ct, reused: true); return; }
            var recoveryProbe = !configuration.GetValue<bool>("VideoImports:TakeoutOnly") && claimed.ErrorCode == YouTubeImportQueue.RateLimited;
            await using var prepared = await media.PrepareAsync(claimed.YoutubeId, missing, async progress =>
            {
                await progressGate.WaitAsync(ct);
                try
                {
                    var percent = Math.Clamp(progress.Percent, 1, 85);
                    if (recoveryProbe && progress.Stage == "transcoding")
                    {
                        await RecoverDownloads(claimed, percent, ct);
                        recoveryProbe = false;
                    }
                    else await Progress(claimed, progress.Stage, percent, ct);
                }
                finally { progressGate.Release(); }
            }, ct);
            if (prepared.DurationMs <= 0 || prepared.DurationMs > durationLimit * 1000 || prepared.SourceHeight <= 0 || prepared.Renditions.Count is < 1 or > 4)
                throw new ApiException(422, "import_invalid_media", "The prepared video exceeds import limits.");
            var title = claimed.RequestedTitle ?? string.Concat(prepared.Title.Where(character => !char.IsControl(character))).Trim();
            if (title.Length == 0) title = "YouTube " + claimed.YoutubeId;
            if (title.Length > 300) title = title[..300];
            var videoId = await EnsureVideo(claimed, title, ct);
            var outputs = prepared.Renditions.OrderByDescending(rendition => rendition.Height).ToArray();
            if (outputs.Select(output => output.Height).Distinct().Count() != outputs.Length) throw new ApiException(422, "import_invalid_media", "Duplicate prepared rendition.");
            for (var index = 0; index < outputs.Length; index++)
            {
                var output = outputs[index];
                var evenFallback = prepared.SourceHeight & ~1;
                var isFallback = missing.All(height => height > prepared.SourceHeight) && output.Height == evenFallback;
                if (output.Height <= 0 || output.Height > prepared.SourceHeight || (!missing.Contains(output.Height) && !isFallback)
                    || output.SizeBytes <= 0 || output.SizeBytes > YouTubeMediaProcessor.FileLimit(configuration, claimed.YoutubeId) || output.DurationMs <= 0 || output.DurationMs > durationLimit * 1000)
                    throw new ApiException(422, "import_invalid_media", "Invalid prepared rendition metadata.");
                await Progress(claimed, "uploading", 86 + index * 12 / outputs.Length, ct);
                var rendition = output.Height.ToString(CultureInfo.InvariantCulture) + "p";
                var current = await db.VideoAssets.AsNoTracking().SingleOrDefaultAsync(asset => asset.VideoId == videoId && asset.Language == "es" && asset.Rendition == rendition, ct);
                if (current is not null)
                {
                    if (!Ready(current)) throw new ApiException(409, "import_asset_conflict", "An existing rendition is not ready.");
                    await RecordExistingHeight(claimed, output.Height, ct); continue;
                }
                var asset = ImportAsset(videoId, output.Height, output.SizeBytes, output.DurationMs, stableUpload);
                await using var stream = new FileStream(output.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length != output.SizeBytes) throw new ApiException(422, "import_invalid_media", "Prepared file size does not match its metadata.");
                await CheckLease(claimed, ct);
                try { await storage.UploadAsync(asset, stream, ct); }
                catch (ApiException exception) when (exception.Status == 409) { throw; }
                catch { if (!stableUpload) await Cleanup(asset); throw; }
                bool created;
                try { created = await PersistAsset(claimed, asset, output.Height, ct); }
                catch
                {
                    db.ChangeTracker.Clear();
                    using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    bool? persisted = null;
                    try { persisted = await db.VideoAssets.AsNoTracking().AnyAsync(value => value.Id == asset.Id, reconcile.Token); }
                    catch (Exception exception) { logger.LogError("Could not reconcile import asset {AssetId}; error {ErrorType}.", asset.Id, exception.GetType().Name); }
                    if (persisted == true) continue;
                    if (persisted == false && !stableUpload) await Cleanup(asset);
                    throw;
                }
                if (!created && !stableUpload) await Cleanup(asset);
            }
            await Complete(claimed, ct);
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                OperationCanceledException => stoppingToken.IsCancellationRequested ? "import_interrupted" : "import_timeout",
                ApiException api when Regex.IsMatch(api.Code, @"\A[a-z0-9_]{1,64}\z") => api.Code,
                _ => "import_failed"
            };
            logger.LogWarning("YouTube import {ImportId} failed with {ErrorCode}; error {ErrorType}.", claimed.Id, code, exception.GetType().Name);
            await Fail(claimed, code, stoppingToken.IsCancellationRequested);
        }
    }

    private VideoAsset ImportAsset(Guid videoId, int height, long size, int duration, bool stableUpload)
    {
        var rendition = height.ToString(CultureInfo.InvariantCulture) + "p";
        var asset = new VideoAsset { VideoId = videoId, Language = "es", Rendition = rendition,
            BlobContainer = UploadContainer(), BlobName = "", SizeBytes = size, DurationMs = duration,
            Status = "ready", CreatedAt = clock.GetUtcNow() };
        var filename = stableUpload ? "import.mp4" : $"{asset.Id:D}.mp4";
        asset.BlobName = $"ejercicios/{videoId:D}/es/{rendition}/{filename}";
        return asset;
    }

    private string UploadContainer()
    {
        var container = configuration["Storage:UploadContainer"] ?? "";
        var allowed = configuration.GetSection("Storage:AllowedContainers").Get<string[]>() ?? ["videos"];
        if (container.Length is < 3 or > 63 || !Regex.IsMatch(container, @"\A[a-z0-9]+(?:-[a-z0-9]+)*\z") || !allowed.Contains(container, StringComparer.Ordinal))
            throw new ApiException(503, "video_storage_unavailable", "Configure an allowed upload container.");
        return container;
    }
    private static bool Ready(VideoAsset asset) => asset.Status == "ready" && asset.SizeBytes > 0 && asset.DurationMs > 0 && asset.ContentType == "video/mp4";
    private async Task<int[]> ReadyHeights(Guid videoId, CancellationToken ct)
    {
        var renditions = await db.VideoAssets.AsNoTracking().Where(asset => asset.VideoId == videoId && asset.Language == "es" && asset.Status == "ready" && asset.SizeBytes > 0 && asset.DurationMs > 0 && asset.ContentType == "video/mp4").Select(asset => asset.Rendition).ToArrayAsync(ct);
        return renditions.Where(value => Regex.IsMatch(value, @"\A[0-9]+p\z")).Select(value => int.TryParse(value[..^1], out var height) ? height : 0).Where(height => height > 0).Distinct().ToArray();
    }
    private IQueryable<VideoImportJob> Owned(VideoImportJob claimed) => db.VideoImportJobs.Where(job => job.Id == claimed.Id && job.Status == "running" && job.LeaseId == claimed.LeaseId && job.LeaseExpiresAt > clock.GetUtcNow());
    private async Task CheckLease(VideoImportJob claimed, CancellationToken ct)
    { if (!await Owned(claimed).AnyAsync(ct)) throw new ApiException(409, "import_interrupted", "The job no longer owns its lease."); }
    private async Task<VideoImportJob> LockJob(VideoImportJob claimed, CancellationToken ct)
    {
        var job = await db.VideoImportJobs.FromSqlInterpolated($"SELECT * FROM videos.video_import_jobs WHERE id = {claimed.Id} FOR UPDATE").SingleAsync(ct);
        if (job.Status != "running" || job.LeaseId != claimed.LeaseId || job.LeaseExpiresAt <= clock.GetUtcNow())
            throw new ApiException(409, "import_interrupted", "The job no longer owns its lease.");
        return job;
    }
    private async Task Progress(VideoImportJob claimed, string stage, int percent, CancellationToken ct)
    {
        if (stage is not ("probing" or "downloading" or "transcoding" or "uploading")) throw new ApiException(422, "import_invalid_media", "Invalid import progress stage.");
        var now = clock.GetUtcNow();
        var changed = await Owned(claimed).Where(job => job.ProgressPercent <= percent).ExecuteUpdateAsync(setters => setters
            .SetProperty(job => job.Stage, stage).SetProperty(job => job.ProgressPercent, percent).SetProperty(job => job.UpdatedAt, now), ct);
        if (changed == 0) await CheckLease(claimed, ct);
    }
    private async Task RecoverDownloads(VideoImportJob claimed, int percent, CancellationToken ct)
    {
        // Release the recovery probe and the other paused jobs only after a successful download.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await Owned(claimed).ExecuteUpdateAsync(setters => setters
            .SetProperty(job => job.Stage, "transcoding").SetProperty(job => job.ProgressPercent, percent)
            .SetProperty(job => job.UpdatedAt, clock.GetUtcNow())
            .SetProperty(job => job.ErrorCode, (string?)null).SetProperty(job => job.ErrorMessage, (string?)null), ct);
        if (changed == 0) throw new ApiException(409, "import_interrupted", "The job no longer owns its lease.");
        await db.VideoImportJobs.Where(job => job.Status == "queued" && job.ErrorCode == YouTubeImportQueue.RateLimited)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.ErrorCode, (string?)null)
                .SetProperty(job => job.ErrorMessage, (string?)null), ct);
        await transaction.CommitAsync(ct);
    }

    private async Task AttachExisting(VideoImportJob claimed, Video video, int[] completed, CancellationToken ct)
    {
        db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJob(claimed, ct);
        var current = await db.Videos.FromSqlInterpolated($"SELECT * FROM videos.videos WHERE id = {video.Id} FOR UPDATE").AsNoTracking().SingleAsync(ct);
        await ValidateExisting(claimed, current, ct);
        job.VideoId = current.Id; job.Title = current.Title; job.CompletedHeights = completed; job.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); db.ChangeTracker.Clear();
    }
    private async Task<Guid> EnsureVideo(VideoImportJob claimed, string title, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJob(claimed, ct); var id = Guid.NewGuid(); var now = clock.GetUtcNow(); var external = YouTubeImportQueue.EffectiveReference(claimed);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO videos.videos (id, external_exercise_id, title, status, created_at, updated_at)
            VALUES ({id}, {external}, {title}, 'draft', {now}, {now}) ON CONFLICT (external_exercise_id) DO NOTHING
            """, ct);
        var video = await db.Videos.FromSqlInterpolated($"SELECT * FROM videos.videos WHERE external_exercise_id = {external} FOR UPDATE").AsNoTracking().SingleAsync(ct);
        if (inserted == 0) await ValidateExisting(claimed, video, ct);
        job.VideoId = video.Id; job.Title = video.Title; job.UpdatedAt = now;
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); db.ChangeTracker.Clear(); return video.Id;
    }
    private async Task ValidateExisting(VideoImportJob claimed, Video video, CancellationToken ct)
    {
        var external = YouTubeImportQueue.EffectiveReference(claimed);
        var origins = await db.VideoImportJobs.AsNoTracking().Where(job => job.VideoId == video.Id).Select(job => job.YoutubeId).Distinct().ToArrayAsync(ct);
        if (video.ExternalExerciseId != external || origins.Any(id => id != claimed.YoutubeId)
            || (external != "youtube:" + claimed.YoutubeId && !origins.Contains(claimed.YoutubeId)))
            throw new ApiException(409, "import_reference_conflict", "This reference belongs to a different or unverified video source.");
        if (claimed.RequestedTitle is not null && claimed.RequestedTitle != video.Title)
            throw new ApiException(409, "import_metadata_conflict", "The existing video has a different title; CSV imports preserve existing titles.");
    }
    private async Task RecordExistingHeight(VideoImportJob claimed, int height, CancellationToken ct)
    {
        db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJob(claimed, ct); job.CompletedHeights = job.CompletedHeights.Append(height).Distinct().OrderDescending().ToArray(); job.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); db.ChangeTracker.Clear();
    }
    private async Task<bool> PersistAsset(VideoImportJob claimed, VideoAsset asset, int height, CancellationToken ct)
    {
        db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJob(claimed, ct);
        var video = await db.Videos.FromSqlInterpolated($"SELECT * FROM videos.videos WHERE id = {asset.VideoId} FOR UPDATE").SingleAsync(ct);
        await ValidateExisting(claimed, video, ct);
        var existing = await db.VideoAssets.AsNoTracking().SingleOrDefaultAsync(value => value.VideoId == asset.VideoId && value.Language == "es" && value.Rendition == asset.Rendition, ct);
        if (existing is not null && !Ready(existing)) throw new ApiException(409, "import_asset_conflict", "An existing rendition is not ready.");
        if (existing is null) db.VideoAssets.Add(asset);
        job.CompletedHeights = job.CompletedHeights.Append(height).Distinct().OrderDescending().ToArray(); job.UpdatedAt = clock.GetUtcNow();
        video.Status = "active"; video.UpdatedAt = job.UpdatedAt;
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); db.ChangeTracker.Clear(); return existing is null;
    }
    private async Task Complete(VideoImportJob claimed, CancellationToken ct, bool reused = false)
    {
        db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJob(claimed, ct);
        if (reused) job.ImportOrigin = null;
        job.Status = "completed"; job.Stage = "completed"; job.ProgressPercent = 100; job.UpdatedAt = clock.GetUtcNow(); job.LeaseId = null; job.LeaseExpiresAt = null;
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); db.ChangeTracker.Clear();
    }
    private async Task Cleanup(VideoAsset asset)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await storage.DeleteAsync(asset, cleanup.Token); }
        catch (Exception exception) { logger.LogError("Could not clean up import asset {AssetId}; error {ErrorType}. Operator cleanup required.", asset.Id, exception.GetType().Name); }
    }
    private async Task Fail(VideoImportJob claimed, string code, bool stopping)
    {
        db.ChangeTracker.Clear(); using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var now = clock.GetUtcNow();
        var paused = code == YouTubeImportQueue.RateLimited;
        var requeue = paused || stopping;
        var message = code switch
        {
            YouTubeImportQueue.RateLimited => "YouTube ha limitado las descargas. Se reanudarán en 5 minutos.",
            "import_interrupted" => "La importación fue interrumpida. Puedes volver a intentarlo.",
            "import_timeout" => "La importación superó el tiempo máximo. Puedes volver a intentarlo.",
            "video_storage_unavailable" => "No se pudo acceder al almacenamiento de vídeos.",
            "import_asset_conflict" => "Ya existe una versión pendiente o deshabilitada con esa resolución.",
            _ => "No se pudo completar la importación. Consulta el código de error y vuelve a intentarlo."
        };
        try
        {
            await db.VideoImportJobs.Where(job => job.Id == claimed.Id && job.Status == "running" && job.LeaseId == claimed.LeaseId).ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, requeue ? "queued" : "failed").SetProperty(job => job.Stage, requeue ? "queued" : "failed")
                .SetProperty(job => job.ErrorCode, job => stopping && !paused ? (job.ErrorCode == YouTubeImportQueue.RateLimited ? job.ErrorCode : null) : code)
                .SetProperty(job => job.ErrorMessage, job => stopping && !paused ? (job.ErrorCode == YouTubeImportQueue.RateLimited ? job.ErrorMessage : null) : message)
                .SetProperty(job => job.UpdatedAt, now).SetProperty(job => job.LeaseId, (Guid?)null).SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null), cleanup.Token);
        }
        catch (Exception exception) { logger.LogError("Could not mark import {ImportId} as failed; error {ErrorType}. Its lease must expire.", claimed.Id, exception.GetType().Name); }
    }
}

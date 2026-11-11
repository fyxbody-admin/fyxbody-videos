using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Api.Localization;
using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FyxBody.Videos.Api.YouTube;

public sealed partial class YouTubeImportQueue(VideosDbContext db, IConfiguration configuration, TimeProvider clock, TakeoutSources? takeout = null)
{
    internal const string RateLimited = "youtube_rate_limited";
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);
    internal static int DownloadSlots(IConfiguration configuration) =>
        Math.Clamp(configuration.GetValue("VideoImports:MaxConcurrentDownloads", 1), 1, 5);
    internal static int TranscodeSlots(IConfiguration configuration) =>
        Math.Clamp(configuration.GetValue("VideoImports:MaxConcurrentTranscodes", 1), 1, 5);
    internal static int JobSlots(IConfiguration configuration) => DownloadSlots(configuration) + TranscodeSlots(configuration) + 1;

    public static VideoImportCapabilities Capabilities(IConfiguration configuration) => new(configuration.GetValue<bool>("VideoImports:Enabled"), [.. YouTubeImportValidation.AvailableHeights],
        [.. YouTubeImportValidation.DefaultHeights], configuration.GetValue<int?>("VideoImports:MaxDurationSeconds") ?? 600, MultipartVideoUpload.MaximumFileBytes);

    public async Task<VideoImportView> Enqueue(VideoImportInput input, CancellationToken ct)
    {
        if (!Capabilities(configuration).Enabled) throw new ApiException(503, "video_imports_disabled", "YouTube imports are not enabled.");
        var minutes = configuration.GetValue<int?>("VideoImports:MaxJobMinutes") ?? 20;
        if (minutes is < 1 or > 20 || Capabilities(configuration).MaxDurationSeconds is < 1 or > 600)
            throw new ApiException(503, "import_configuration", "Invalid video import limits.");
        var (id, url, heights) = YouTubeImportValidation.Input(input);
        try { return await EnqueueOnce(id, url, heights, ct); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_video_import_jobs_youtube_id" })
        {
            db.ChangeTracker.Clear();
            var concurrent = await Active(id, ct);
            if (concurrent is not null && EffectiveReference(concurrent) == "youtube:" + id && concurrent.RequestedTitle is null) return ActiveView(concurrent, heights);
            throw new ApiException(409, "resource_conflict", "The import queue changed; retry the request.");
        }
    }
    private Task<VideoImportJob?> Active(string id, CancellationToken ct) => db.VideoImportJobs.AsNoTracking().SingleOrDefaultAsync(job => job.YoutubeId == id && (job.Status == "queued" || job.Status == "running"), ct);
    private static VideoImportView ActiveView(VideoImportJob job, int[] heights)
    {
        if (heights.Except(job.RequestedHeights).Any())
            throw new ApiException(409, "import_in_progress", "Espera a que termine la importación actual para solicitar otras resoluciones.");
        return View(job);
    }
    private async Task<VideoImportView> EnqueueOnce(string id, string url, int[] heights, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockIdentities([id, "reference:youtube:" + id], ct);
        var existing = await Active(id, ct);
        if (existing is not null) {
            if (EffectiveReference(existing) != "youtube:" + id || existing.RequestedTitle is not null) throw new ApiException(409, "resource_conflict", "The active import has different reference or title metadata.");
            var view = ActiveView(existing, heights); await transaction.CommitAsync(ct); return view;
        }
        var row = new VideoImportCsvRow { YoutubeId = id, EffectiveReference = "youtube:" + id };
        await CheckRows([row], ct);
        if (row.Errors.Count > 0) throw new ApiException(409, "resource_conflict", row.Errors[0].Message);
        var now = clock.GetUtcNow();
        var job = new VideoImportJob { YoutubeId = id, SourceUrl = url, Reference = "youtube:" + id, RequestedHeights = heights, CreatedAt = now, UpdatedAt = now };
        db.VideoImportJobs.Add(job); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return View(job);
    }
    public async Task<VideoImportProgress?> Progress(CancellationToken ct)
    {
        var total = configuration.GetValue<int>("VideoImports:CampaignTotal");
        if (total <= 0) return null;
        var now = clock.GetUtcNow();
        var hourAgo = now.AddHours(-1);
        var counts = await db.VideoImportJobs.AsNoTracking()
            .GroupBy(_ => 1).Select(jobs => new
            {
                Completed = jobs.Count(job => job.Status == "completed"),
                Failed = jobs.Count(job => job.Status == "failed"),
                Local = jobs.Count(job => job.Status == "completed" && job.ImportOrigin == "local_mp4"),
                Vivaldi = jobs.Count(job => job.Status == "completed" && job.ImportOrigin == "vivaldi"),
                Other = jobs.Count(job => job.Status == "completed" && job.ImportOrigin == "youtube"),
                Recent = jobs.Count(job => job.Status == "completed" && job.UpdatedAt > hourAgo)
            }).SingleOrDefaultAsync(ct);
        var completed = counts?.Completed ?? 0;
        var failed = counts?.Failed ?? 0;
        var recent = counts?.Recent ?? 0;
        var remaining = Math.Max(0, total - completed - failed);
        return new(total, completed, failed, recent, remaining > 0 && recent > 0
            ? now.AddHours((double)remaining / recent) : null,
            counts?.Local ?? 0, counts?.Vivaldi ?? 0, counts?.Other ?? 0,
            completed - (counts?.Local ?? 0) - (counts?.Vivaldi ?? 0) - (counts?.Other ?? 0));
    }
    public async Task<AdminPage<VideoImportView>> List(int? page, int? pageSize, CancellationToken ct, string? filter = null, string? search = null)
    {
        var (p, size) = AdminValidation.Pagination(page, pageSize);
        var query = db.VideoImportJobs.AsNoTracking();
        query = filter switch
        {
            null => query,
            "running" => query.Where(job => job.Status == "running"),
            "pending" => query.Where(job => job.Status == "queued" || job.Status == "failed"),
            "completed" => query.Where(job => job.Status == "completed"),
            _ => throw new ApiException(400, "invalid_request", "The request is invalid.")
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = AdminValidation.Text(search, 512, "search");
            if (term.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try { term = YouTubeImportValidation.Input(new(term)).YoutubeId; }
                catch (ApiException) { /* Search the literal URL if it cannot be normalized. */ }
            }
            query = query.Where(job => (job.Reference ?? ("youtube:" + job.YoutubeId)).Contains(term)
                || job.SourceUrl.Contains(term) || job.YoutubeId.Contains(term));
        }
        var jobs = await query.OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id).Skip((p - 1) * size).Take(size).ToArrayAsync(ct);
        return new(jobs.Select(View).ToArray(), p, size, await query.CountAsync(ct));
    }
    public async Task<VideoImportView> Get(Guid id, CancellationToken ct) => View(await db.VideoImportJobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == id, ct)
        ?? throw new ApiException(404, "not_found", "No se ha encontrado la importación."));

    public async Task<int> ExpireLeases(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.VideoImportJobs.Where(job => job.Status == "running" && job.LeaseExpiresAt <= now).ExecuteUpdateAsync(setters => setters
            .SetProperty(job => job.Status, "queued").SetProperty(job => job.Stage, "queued").SetProperty(job => job.UpdatedAt, now)
            .SetProperty(job => job.ErrorCode, job => job.ErrorCode == RateLimited ? RateLimited : null)
            .SetProperty(job => job.ErrorMessage, job => job.ErrorCode == RateLimited ? job.ErrorMessage : null)
            .SetProperty(job => job.LeaseId, (Guid?)null).SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null), ct);
    }
    public async Task<VideoImportJob?> Claim(CancellationToken ct)
    {
        var localOnly = takeout?.Enabled == true;
        var origin = localOnly ? "local_mp4" : OperatingSystem.IsMacOS() && configuration.GetValue<bool>("VideoImports:UseLocalVivaldi") ? "vivaldi" : "youtube";
        var localIds = localOnly ? takeout!.Ids : [];
        var excludedLocalIds = !localOnly && OperatingSystem.IsMacOS()
            && configuration.GetValue<bool>("VideoImports:PreferLocalMp4") && takeout is not null
            ? takeout.AvailableIds : [];
        if (localOnly && localIds.Length == 0) return null;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize admission so parallel workers cannot all see the same free slots.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1953342731)", ct);
        var lease = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(30);
        var claimed = (await db.VideoImportJobs.FromSqlInterpolated($"""
            WITH queue_state AS (
                SELECT
                    count(*) FILTER (WHERE status = 'running') AS active_jobs,
                    count(*) FILTER (WHERE status = 'running' AND stage IN ('probing', 'downloading')) AS active_downloads,
                    coalesce(bool_or(status = 'running' AND error_code = {RateLimited}), false) AS testing_recovery,
                    max(updated_at) FILTER (WHERE status = 'queued' AND error_code = {RateLimited}) AS limited_at
                FROM videos.video_import_jobs
                WHERE status IN ('queued', 'running')
            ), next AS (
                SELECT job.id FROM videos.video_import_jobs job CROSS JOIN queue_state state
                WHERE job.status = 'queued'
                    AND state.active_jobs < {JobSlots(configuration)}
                    AND state.active_downloads < {DownloadSlots(configuration)}
                    AND (
                        ({localOnly} AND job.youtube_id = ANY({localIds})
                            AND (job.error_code IS DISTINCT FROM {RateLimited} OR job.updated_at <= {now.Subtract(Cooldown)}))
                        OR (NOT {localOnly} AND NOT (job.youtube_id = ANY({excludedLocalIds})) AND NOT state.testing_recovery
                            AND (state.limited_at IS NULL OR (
                                state.limited_at <= {now.Subtract(Cooldown)}
                                AND state.active_downloads = 0 AND job.error_code = {RateLimited}
                            )))
                    )
                ORDER BY job.created_at, job.id FOR UPDATE OF job SKIP LOCKED LIMIT 1
            )
            UPDATE videos.video_import_jobs AS job
            SET status = 'running', stage = 'probing', progress_percent = 1, import_origin = {origin},
                lease_id = {lease}, lease_expires_at = {expires}, updated_at = {now}
            FROM next WHERE job.id = next.id RETURNING job.*
            """).AsNoTracking().ToArrayAsync(ct)).SingleOrDefault();
        await transaction.CommitAsync(ct);
        return claimed;
    }
    public static VideoImportView View(VideoImportJob job) => new(job.Id, job.YoutubeId, job.SourceUrl, job.Title ?? job.RequestedTitle, job.Status, job.Stage,
        job.ProgressPercent, job.VideoId, job.RequestedHeights, job.CompletedHeights, job.CreatedAt, job.UpdatedAt, job.ErrorCode, ApiMessageCatalog.JobError(job.ErrorCode, job.ErrorMessage, job.Status), EffectiveReference(job), job.ImportOrigin);
}

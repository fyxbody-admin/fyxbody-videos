using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FyxBody.Videos.Api.YouTube;

public sealed partial class YouTubeImportQueue
{
    public static string EffectiveReference(VideoImportJob job) => job.Reference ?? "youtube:" + job.YoutubeId;
    public async Task<VideoImportCsvReport> Csv(VideoImportCsvInput input, bool enqueue, CancellationToken ct)
    {
        if (!Capabilities(configuration).Enabled) throw new ApiException(503, "video_imports_disabled", "YouTube imports are not enabled.");
        var minutes = configuration.GetValue<int?>("VideoImports:MaxJobMinutes") ?? 20;
        if (minutes is < 1 or > 20 || Capabilities(configuration).MaxDurationSeconds is < 1 or > 600)
            throw new ApiException(503, "import_configuration", "Invalid video import limits.");
        var report = VideoImportCsv.Parse(input.Csv);
        if (!report.CanImport) return report;
        if (!enqueue) { await CheckRows(report.Rows, ct); return VideoImportCsv.Report(report.Rows.ToList(), report.Errors.ToList()); }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockIdentities(report.Rows.SelectMany(row => new[] { row.YoutubeId!, "reference:" + row.EffectiveReference! }), ct);
        await CheckRows(report.Rows, ct);
        if (report.Rows.Any(row => row.Errors.Count > 0)) return VideoImportCsv.Report(report.Rows.ToList(), report.Errors.ToList());
        var now = clock.GetUtcNow();
        foreach (var row in report.Rows.Where(row => !row.Reused))
        {
            var job = new VideoImportJob { YoutubeId = row.YoutubeId!, SourceUrl = row.Url, Reference = row.EffectiveReference, RequestedTitle = row.Title,
                RequestedHeights = row.Heights, CreatedAt = now, UpdatedAt = now };
            db.VideoImportJobs.Add(job); row.JobId = job.Id;
        }
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(ct); db.ChangeTracker.Clear();
            foreach (var row in report.Rows) { row.JobId = null; row.Reused = false; }
            return VideoImportCsv.Report(report.Rows.ToList(), [VideoImportCsv.Error("csv_queue_changed", "The import queue changed; preview the CSV again before importing.")]);
        }
        return VideoImportCsv.Report(report.Rows.ToList(), [], queued: true);
    }
    private async Task LockIdentities(IEnumerable<string> identities, CancellationToken ct)
    {
        // Single and bulk requests take exactly the same globally ordered locks.
        foreach (var key in identities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }
    private async Task CheckRows(IReadOnlyList<VideoImportCsvRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(row => row.YoutubeId!).Distinct().ToArray(); var references = rows.Select(row => row.EffectiveReference!).Distinct().ToArray();
        var active = await db.VideoImportJobs.AsNoTracking().Where(job => (job.Status == "queued" || job.Status == "running")
            && (ids.Contains(job.YoutubeId) || references.Contains(job.Reference!))).ToArrayAsync(ct);
        var videos = await db.Videos.AsNoTracking().Where(video => references.Contains(video.ExternalExerciseId)).ToArrayAsync(ct);
        var videoIds = videos.Select(video => video.Id).ToArray();
        var origins = await db.VideoImportJobs.AsNoTracking().Where(job => job.VideoId != null && videoIds.Contains(job.VideoId.Value))
            .Select(job => new { job.VideoId, job.YoutubeId }).Distinct().ToArrayAsync(ct);
        foreach (var row in rows)
        {
            var reserved = active.FirstOrDefault(job => EffectiveReference(job) == row.EffectiveReference && job.YoutubeId != row.YoutubeId);
            if (reserved is not null) row.Error("csv_reference_conflict", "This reference is reserved by another YouTube video.");
            var running = active.FirstOrDefault(job => job.YoutubeId == row.YoutubeId);
            if (running is not null)
            {
                if (EffectiveReference(running) != row.EffectiveReference || running.RequestedTitle != row.Title)
                    row.Error("csv_metadata_conflict", "The active import has different reference or title metadata.");
                else if (row.Heights.Except(running.RequestedHeights).Any())
                    row.Error("csv_import_in_progress", "Wait for the active import before requesting additional resolutions.");
                else { row.JobId = running.Id; row.Reused = true; }
            }
            var video = videos.SingleOrDefault(video => video.ExternalExerciseId == row.EffectiveReference);
            if (video is null) continue;
            var known = origins.Where(origin => origin.VideoId == video.Id).ToArray();
            var canonicalLegacy = video.ExternalExerciseId == "youtube:" + row.YoutubeId;
            if (known.Any(origin => origin.YoutubeId != row.YoutubeId) || (!canonicalLegacy && !known.Any(origin => origin.YoutubeId == row.YoutubeId)))
                row.Error("csv_reference_conflict", "This reference belongs to a different or unverified video source.");
            if (row.Title is not null && row.Title != video.Title)
                row.Error("csv_metadata_conflict", "The existing video has a different title; CSV imports preserve existing titles.");
        }
    }
}

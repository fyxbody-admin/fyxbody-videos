using System.Data;
using System.Globalization;
using System.Text;
using FyxBody.Videos.Api.Partner;
using FyxBody.Videos.Api.YouTube;
using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PartnerEntity = FyxBody.Videos.Data.Partner;

namespace FyxBody.Videos.Api.Admin;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1").RequireAuthorization("admin").RequireRateLimiting("admin").WithTags("Administration").WithGroupName("admin");
        group.MapGet("/video-imports/capabilities", (IConfiguration configuration) => YouTubeImportQueue.Capabilities(configuration));
        group.MapPost("/video-imports", async (VideoImportInput input, HttpContext context, IConfiguration configuration, CancellationToken ct) => {
            if (!configuration.GetValue<bool>("VideoImports:Enabled")) throw new ApiException(503, "video_imports_disabled", "YouTube imports are not enabled.");
            var imports = context.RequestServices.GetRequiredService<YouTubeImportQueue>();
            var job = await imports.Enqueue(input, ct);
            return TypedResults.Accepted($"/admin/v1/video-imports/{job.Id}", job);
        }).WithSummary("Queue a YouTube video import").WithDescription("Queues a durable background import. An active job for the same canonical YouTube video ID is returned again only when its requested heights cover this request; otherwise returns 409 import_in_progress. Failed jobs require an explicit new POST to retry; existing ready renditions are reused.");
        group.MapPost("/video-imports/csv/preview", (HttpRequest request, YouTubeImportQueue imports, CancellationToken ct) => ImportCsv(request, imports, false, ct))
            .WithSummary("Validate a CSV import without enqueueing videos");
        group.MapPost("/video-imports/csv", (HttpRequest request, YouTubeImportQueue imports, CancellationToken ct) => ImportCsv(request, imports, true, ct))
            .WithSummary("Atomically queue all validated CSV rows");
        group.MapGet("/video-imports", (int? page, int? pageSize, string? filter, string? search, YouTubeImportQueue imports, CancellationToken ct) => imports.List(page, pageSize, ct, filter, search));
        group.MapGet("/video-imports/progress", async (YouTubeImportQueue imports, CancellationToken ct) =>
        {
            var progress = await imports.Progress(ct);
            return progress is null ? Results.NoContent() : Results.Ok(progress);
        });
        group.MapGet("/video-imports/{importId:guid}", (Guid importId, YouTubeImportQueue imports, CancellationToken ct) => imports.Get(importId, ct));
        group.MapGet("/partners", ListPartners);
        group.MapPost("/partners", CreatePartner);
        group.MapGet("/partners/{partnerId:guid}", GetPartner);
        group.MapPut("/partners/{partnerId:guid}", UpdatePartner);
        group.MapGet("/partners/{partnerId:guid}/credentials", ListCredentials);
        group.MapPost("/partners/{partnerId:guid}/credentials", CreateCredential);
        group.MapDelete("/partners/{partnerId:guid}/credentials/{credentialId:guid}", RevokeCredential);
        group.MapGet("/partners/{partnerId:guid}/licenses", ListLicenses);
        group.MapPost("/partners/{partnerId:guid}/licenses", CreateLicense).WithDescription("unlimitedSessions defaults to false when omitted or null. Unlimited licenses store monthlySessionQuota as zero while retaining session usage counters.");
        group.MapGet("/partners/{partnerId:guid}/licenses/{licenseId:guid}", GetLicense);
        group.MapPut("/partners/{partnerId:guid}/licenses/{licenseId:guid}", UpdateLicense).WithDescription("Omitting unlimitedSessions or sending null preserves the existing mode. When unlimited, monthlySessionQuota is normalized to zero; send false with a nonnegative quota to return to a limited license.");
        group.MapGet("/partners/{partnerId:guid}/licenses/{licenseId:guid}/catalogue", GetCatalogue);
        group.MapPut("/partners/{partnerId:guid}/licenses/{licenseId:guid}/catalogue", ReplaceCatalogue);
        group.MapPost("/partners/{partnerId:guid}/licenses/{licenseId:guid}/catalogue", AppendCatalogue)
            .WithSummary("Append up to 1000 video grants to a catalogue");
        group.MapGet("/partners/{partnerId:guid}/catalogue", GetEffectiveCatalogue);
        group.MapGet("/partners/{partnerId:guid}/usage", GetUsage);
        group.MapGet("/partners/{partnerId:guid}/usage.csv", ExportUsage);
        group.MapGet("/videos", ListVideos);
        group.MapGet("/videos/search", SearchVideos);
        group.MapPost("/videos", CreateVideo);
        group.MapGet("/videos/{videoId:guid}", GetVideo);
        group.MapPut("/videos/{videoId:guid}", UpdateVideo);
        group.MapGet("/videos/{videoId:guid}/assets", ListAssets);
        group.MapPost("/videos/{videoId:guid}/assets", CreateAsset);
        group.MapPost("/videos/{videoId:guid}/assets/upload", UploadAsset)
            .Accepts<AssetUploadInput>("multipart/form-data")
            .WithSummary("Upload an MP4 asset for a video")
            .WithDescription("Multipart file, language, rendition, durationMs. Maximum MP4 file size 200 MiB. Duration is client-supplied metadata. Administrator cookie requests require X-CSRF-TOKEN. The backend creates a private Azure blob and ready asset without consuming partner quota.")
            .ProducesProblem(413).ProducesProblem(504);
        group.MapGet("/videos/{videoId:guid}/assets/{assetId:guid}", GetAsset);
        group.MapPut("/videos/{videoId:guid}/assets/{assetId:guid}", UpdateAsset);
        group.MapPost("/videos/{videoId:guid}/assets/{assetId:guid}/preview", PreviewAsset)
            .WithSummary("Preview a ready asset for administration")
            .WithDescription("Returns a ten-minute read URL for an administrator, including draft/archived videos. Does not create playback sessions, events or quota usage; Azure transfer costs still apply. The URL is confidential and the response is not cacheable.");
        return endpoints;
    }

    private static async Task<VideoImportCsvReport> ImportCsv(HttpRequest request, YouTubeImportQueue imports, bool enqueue, CancellationToken ct)
    {
        if (!request.HasJsonContentType()) throw new ApiException(415, "invalid_request", "The request must use application/json.");
        var bodyLimit = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = VideoImportCsv.MaximumBodyBytes;
        if (request.ContentLength > VideoImportCsv.MaximumBodyBytes) throw new ApiException(413, "upload_too_large", "The CSV request must not exceed 2 MiB.");
        using var buffer = new MemoryStream(); var chunk = new byte[16384];
        while (true)
        {
            int read;
            try { read = await request.Body.ReadAsync(chunk, ct); }
            catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
            { throw new ApiException(413, "upload_too_large", "The CSV request must not exceed 2 MiB."); }
            if (read == 0) break;
            if (buffer.Length + read > VideoImportCsv.MaximumBodyBytes) throw new ApiException(413, "upload_too_large", "The CSV request must not exceed 2 MiB.");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        VideoImportCsvInput? input;
        try { input = await System.Text.Json.JsonSerializer.DeserializeAsync<VideoImportCsvInput>(buffer, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web), ct); }
        catch (System.Text.Json.JsonException) { throw new ApiException(400, "invalid_request", "The CSV request must contain a JSON object with a csv string."); }
        if (input?.Csv is null) throw new ApiException(400, "invalid_request", "The CSV request must contain a JSON object with a csv string.");
        return await imports.Csv(input, enqueue, ct);
    }

    private static async Task<AdminPage<PartnerView>> ListPartners(int? page, int? pageSize, VideosDbContext db, CancellationToken ct)
    {
        var (p, size) = AdminValidation.Pagination(page, pageSize);
        var query = db.Partners.AsNoTracking();
        return new(await query.OrderBy(x => x.Code).ThenBy(x => x.Id).Skip((p - 1) * size).Take(size).Select(x => new PartnerView(x.Id, x.Code, x.Name, x.Status, x.CreatedAt, x.ParentPartnerId)).ToListAsync(ct), p, size, await query.CountAsync(ct));
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Created<PartnerView>> CreatePartner(PartnerInput input, VideosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        input = AdminValidation.Partner(input);
        var partner = new PartnerEntity { Code = input.Code, Name = input.Name, Status = input.Status, CreatedAt = clock.GetUtcNow() };
        db.Partners.Add(partner);
        await Save(db, ct);
        return TypedResults.Created($"/admin/v1/partners/{partner.Id}", View(partner));
    }

    private static async Task<PartnerView> GetPartner(Guid partnerId, VideosDbContext db, CancellationToken ct) => View(await FindPartner(db, partnerId, ct));

    private static async Task<PartnerView> UpdatePartner(Guid partnerId, PartnerInput input, VideosDbContext db, CancellationToken ct)
    {
        input = AdminValidation.Partner(input);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var partner = await LockPartner(db, partnerId, ct);
        partner.Code = input.Code; partner.Name = input.Name; partner.Status = input.Status;
        await Save(db, ct); await tx.CommitAsync(ct);
        return View(partner);
    }

    private static async Task<AdminPage<CredentialView>> ListCredentials(Guid partnerId, int? page, int? pageSize, VideosDbContext db, CancellationToken ct)
    {
        var (p, size) = AdminValidation.Pagination(page, pageSize);
        await FindPartner(db, partnerId, ct);
        var query = db.PartnerCredentials.AsNoTracking().Where(x => x.PartnerId == partnerId);
        return new(await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((p - 1) * size).Take(size).Select(x => new CredentialView(x.Id, x.KeyId, x.CreatedAt, x.ExpiresAt, x.RevokedAt)).ToListAsync(ct), p, size, await query.CountAsync(ct));
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Created<CreatedCredentialView>> CreateCredential(Guid partnerId, CredentialInput input, VideosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (input.ExpiresAt is { } expires && (expires.Offset != TimeSpan.Zero || expires <= now)) throw Invalid("expiresAt debe estar en UTC y ser futuro.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockPartner(db, partnerId, ct);
        var (keyId, secret, hash) = CredentialSecrets.Generate();
        var credential = new PartnerCredential { PartnerId = partnerId, KeyId = keyId, SecretHash = hash, CreatedAt = now, ExpiresAt = input.ExpiresAt };
        db.PartnerCredentials.Add(credential);
        await Save(db, ct); await tx.CommitAsync(ct);
        return TypedResults.Created($"/admin/v1/partners/{partnerId}/credentials", new CreatedCredentialView(View(credential), $"{keyId}.{secret}"));
    }

    private static async Task<CredentialView> RevokeCredential(Guid partnerId, Guid credentialId, VideosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockPartner(db, partnerId, ct);
        var credential = await db.PartnerCredentials.SingleOrDefaultAsync(x => x.Id == credentialId && x.PartnerId == partnerId, ct) ?? throw Missing("credential");
        credential.RevokedAt ??= clock.GetUtcNow();
        await Save(db, ct); await tx.CommitAsync(ct);
        return View(credential);
    }

    private static async Task<AdminPage<LicenseView>> ListLicenses(Guid partnerId, int? page, int? pageSize, VideosDbContext db, CancellationToken ct)
    {
        var (p, size) = AdminValidation.Pagination(page, pageSize);
        await FindPartner(db, partnerId, ct);
        var query = db.Licenses.AsNoTracking().Where(x => x.PartnerId == partnerId);
        return new(await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((p - 1) * size).Take(size).Select(x => new LicenseView(x.Id, x.PartnerId, x.Name, x.Status, x.StartsAt, x.EndsAt, x.MonthlySessionQuota, x.CreatedAt, x.UnlimitedSessions, x.AllowSubpartnerManagement, x.AllowCatalogueDelegation, x.AllowPartnerUploads, x.UploadQuotaMode, x.UploadQuotaLimit)).ToListAsync(ct), p, size, await query.CountAsync(ct));
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Created<LicenseView>> CreateLicense(Guid partnerId, LicenseInput input, VideosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        input = AdminValidation.License(input);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockPartner(db, partnerId, ct);
        var partner = await FindPartner(db, partnerId, ct);
        var license = new License { PartnerId = partnerId, CreatedAt = clock.GetUtcNow() };
        ApplyLicense(input, license, partner);
        db.Licenses.Add(license);
        await Save(db, ct); await tx.CommitAsync(ct);
        return TypedResults.Created($"/admin/v1/partners/{partnerId}/licenses/{license.Id}", View(license));
    }

    private static async Task<LicenseView> GetLicense(Guid partnerId, Guid licenseId, VideosDbContext db, CancellationToken ct) => View(await FindLicense(db, partnerId, licenseId, ct));

    private static async Task<LicenseView> UpdateLicense(Guid partnerId, Guid licenseId, LicenseInput input, VideosDbContext db, CancellationToken ct)
    {
        input = AdminValidation.License(input);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var partner = await LockPartner(db, partnerId, ct);
        var license = await FindLicense(db, partnerId, licenseId, ct);
        ApplyLicense(input, license, partner);
        await Save(db, ct); await tx.CommitAsync(ct);
        return View(license);
    }

    private static async Task<CatalogueView> GetCatalogue(Guid partnerId, Guid licenseId, VideosDbContext db, CancellationToken ct)
    {
        await FindLicense(db, partnerId, licenseId, ct);
        return new(await db.LicenseVideos.AsNoTracking().Where(x => x.LicenseId == licenseId && x.RevokedAt == null).OrderBy(x => x.VideoId).Select(x => x.VideoId).ToArrayAsync(ct));
    }

    private static Task<CatalogueView> ReplaceCatalogue(Guid partnerId, Guid licenseId, CatalogueInput input, VideosDbContext db, TimeProvider clock, CancellationToken ct) =>
        WriteCatalogue(partnerId, licenseId, input, db, clock, replace: true, ct);

    private static Task<CatalogueView> AppendCatalogue(Guid partnerId, Guid licenseId, CatalogueInput input, VideosDbContext db, TimeProvider clock, CancellationToken ct) =>
        WriteCatalogue(partnerId, licenseId, input, db, clock, replace: false, ct);

    private static async Task<CatalogueView> WriteCatalogue(Guid partnerId, Guid licenseId, CatalogueInput input, VideosDbContext db, TimeProvider clock, bool replace, CancellationToken ct)
    {
        var ids = AdminValidation.Catalogue(input.VideoIds);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var partner = await LockPartner(db, partnerId, ct);
        await FindLicense(db, partnerId, licenseId, ct);
        var videos = await db.Videos.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync(ct);
        if (videos.Length != ids.Length) throw Invalid("Todos los videoIds deben existir.");
        var existing = await db.LicenseVideos.Where(x => x.LicenseId == licenseId).ToListAsync(ct);
        var known = existing.ToDictionary(x => x.VideoId);
        foreach (var video in videos)
        {
            var current = known.GetValueOrDefault(video.Id);
            if (current is { SourceLicenseId: not null, RevokedAt: null }) continue;
            if (video.OwnerPartnerId != partnerId && (video.OwnerPartnerId != null || partner.ParentPartnerId != null))
                throw new ApiException(403, "catalogue_delegation_forbidden", "Assign this video through the principal's delegated catalogue.");
        }
        var requested = ids.ToHashSet();
        var now = clock.GetUtcNow();
        foreach (var grant in existing)
        {
            if (requested.Contains(grant.VideoId)) grant.RevokedAt = null;
            else if (replace) grant.RevokedAt ??= now;
        }
        db.LicenseVideos.AddRange(ids.Where(id => !known.ContainsKey(id)).Select(id => new LicenseVideo { LicenseId = licenseId, VideoId = id, CreatedAt = now }));
        await Save(db, ct); await tx.CommitAsync(ct);
        return new(ids.Order().ToArray());
    }

    private static async Task<AdminPage<VideoView>> GetEffectiveCatalogue(Guid partnerId, int? page, int? pageSize, VideosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (p, size) = AdminValidation.Pagination(page, pageSize);
        var partner = await FindPartner(db, partnerId, ct);
        var now = clock.GetUtcNow();
        var query = CatalogueAccess.VisibleVideos(db, partner.Id, now);
        return await VideoPage(query, p, size, ct);
    }

    private static async Task<UsageResponse> GetUsage(Guid partnerId, string? month, VideosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var current = clock.GetUtcNow();
        month ??= $"{current.Year:0000}-{current.Month:00}";
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) || start.Year >= 9999)
            throw Invalid("month debe usar YYYY-MM, con año anterior a 9999.");
        await FindPartner(db, partnerId, ct);
        var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddMonths(1);
        var licenses = await db.Licenses.AsNoTracking().Where(x => x.PartnerId == partnerId).OrderBy(x => x.Id).ToArrayAsync(ct);
        var counters = await db.UsageMonthly.AsNoTracking().Where(x => x.PartnerId == partnerId && x.Month == start).ToArrayAsync(ct);
        var events = db.PlaybackEvents.AsNoTracking().Where(e => e.OccurredAt >= from && e.OccurredAt < to && db.PlaybackSessions.Any(s => s.Id == e.PlaybackSessionId && s.PartnerId == partnerId));
        return new(month, licenses.Select(x => new LicenseUsageResponse(x.Id, x.MonthlySessionQuota, counters.SingleOrDefault(c => c.LicenseId == x.Id)?.SessionsIssued ?? 0, x.Name, x.UnlimitedSessions)).ToArray(),
            await events.Where(x => x.Kind == "started").Select(x => x.PlaybackSessionId).Distinct().CountAsync(ct),
            await events.Where(x => x.Kind == "completed").Select(x => x.PlaybackSessionId).Distinct().CountAsync(ct));
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult> ExportUsage(Guid partnerId, string? month, VideosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var usage = await GetUsage(partnerId, month, db, clock, ct);
        var csv = new StringBuilder("record_type,month,partner_id,license_id,monthly_session_quota,sessions_issued,started_sessions,completed_sessions,unlimited_sessions\r\n");
        foreach (var license in usage.Licenses)
            CsvRow(csv, "license", usage.Month, partnerId.ToString("D"), license.LicenseId.ToString("D"), license.MonthlySessionQuota.ToString(CultureInfo.InvariantCulture), license.SessionsIssued.ToString(CultureInfo.InvariantCulture), "", "", license.UnlimitedSessions ? "true" : "false");
        CsvRow(csv, "partner_events", usage.Month, partnerId.ToString("D"), "", "", "", usage.StartedSessions.ToString(CultureInfo.InvariantCulture), usage.CompletedSessions.ToString(CultureInfo.InvariantCulture), "");
        return TypedResults.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"usage-{partnerId:D}-{usage.Month}.csv");
    }

    private static void CsvRow(StringBuilder csv, params string[] cells)
    {
        csv.AppendJoin(',', cells.Select(value => "\"" + value.Replace("\"", "\"\"") + "\""));
        csv.Append("\r\n");
    }

    private static async Task<AdminPage<VideoView>> ListVideos(int? page, int? pageSize, VideosDbContext db, CancellationToken ct)
    {
        var (p, size) = AdminValidation.Pagination(page, pageSize);
        return await VideoPage(db.Videos.AsNoTracking(), p, size, ct);
    }

    private static async Task<AdminPage<VideoView>> SearchVideos(string? search, int? page, int? pageSize, VideosDbContext db, CancellationToken ct)
    {
        var (p, size) = AdminValidation.Pagination(page, pageSize);
        var query = db.Videos.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = AdminValidation.Text(search, 512, "search").ToLowerInvariant();
            query = query.Where(video => video.Title.ToLower().Contains(term) || video.ExternalExerciseId.ToLower().Contains(term));
        }
        return await VideoPage(query, p, size, ct);
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Created<VideoView>> CreateVideo(VideoInput input, VideosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        input = AdminValidation.Video(input);
        if (input.ExternalExerciseId.StartsWith("private:", StringComparison.OrdinalIgnoreCase))
            throw Invalid("The private: namespace is reserved for partner uploads.");
        var now = clock.GetUtcNow();
        var video = new Video { ExternalExerciseId = input.ExternalExerciseId, Title = input.Title, Status = input.Status, CreatedAt = now, UpdatedAt = now };
        db.Videos.Add(video); await Save(db, ct);
        return TypedResults.Created($"/admin/v1/videos/{video.Id}", View(video));
    }

    private static async Task<VideoView> GetVideo(Guid videoId, VideosDbContext db, CancellationToken ct) => View(await db.Videos.AsNoTracking().SingleOrDefaultAsync(x => x.Id == videoId, ct) ?? throw Missing("video"));

    private static async Task<VideoView> UpdateVideo(Guid videoId, VideoInput input, VideosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        input = AdminValidation.Video(input);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var video = await LockVideo(db, videoId, ct);
        if ((video.OwnerPartnerId != null && input.ExternalExerciseId != video.ExternalExerciseId)
            || (video.OwnerPartnerId == null && input.ExternalExerciseId.StartsWith("private:", StringComparison.OrdinalIgnoreCase)))
            throw Invalid("Private upload references are immutable.");
        video.ExternalExerciseId = input.ExternalExerciseId; video.Title = input.Title; video.Status = input.Status; video.UpdatedAt = clock.GetUtcNow();
        await Save(db, ct); await tx.CommitAsync(ct);
        return View(video);
    }

    private static async Task<AdminPage<AssetView>> ListAssets(Guid videoId, int? page, int? pageSize, VideosDbContext db, CancellationToken ct)
    {
        var (p, size) = AdminValidation.Pagination(page, pageSize);
        if (!await db.Videos.AnyAsync(x => x.Id == videoId, ct)) throw Missing("video");
        var query = db.VideoAssets.AsNoTracking().Where(x => x.VideoId == videoId);
        return new(await query.OrderBy(x => x.Language).ThenBy(x => x.Rendition).ThenBy(x => x.Id).Skip((p - 1) * size).Take(size).Select(x => new AssetView(x.Id, x.VideoId, x.Language, x.Rendition, x.BlobContainer, x.BlobName, x.ContentType, x.SizeBytes, x.DurationMs, x.Status, x.CreatedAt)).ToListAsync(ct), p, size, await query.CountAsync(ct));
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Created<AssetView>> CreateAsset(Guid videoId, AssetInput input, VideosDbContext db, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        input = AdminValidation.Asset(input);
        CheckContainer(input.BlobContainer, config);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var video = await LockVideo(db, videoId, ct);
        RequireSharedAsset(video);
        var now = clock.GetUtcNow();
        var asset = new VideoAsset { VideoId = videoId, CreatedAt = now };
        Apply(asset, input); video.UpdatedAt = now;
        db.VideoAssets.Add(asset); await Save(db, ct); await tx.CommitAsync(ct);
        return TypedResults.Created($"/admin/v1/videos/{videoId}/assets/{asset.Id}", View(asset));
    }

    private static async Task<AssetView> GetAsset(Guid videoId, Guid assetId, VideosDbContext db, CancellationToken ct) => View(await db.VideoAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId && x.VideoId == videoId, ct) ?? throw Missing("asset"));

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Created<AssetView>> UploadAsset(Guid videoId, HttpRequest request, AdminVideoUploadService uploads, CancellationToken ct)
    {
        var asset = await uploads.UploadAsync(videoId, request, ct);
        return TypedResults.Created($"/admin/v1/videos/{videoId}/assets/{asset.Id}", asset);
    }

    private static async Task<AssetPreviewView> PreviewAsset(Guid videoId, Guid assetId, VideosDbContext db, IVideoUrlSigner signer, TimeProvider clock, CancellationToken ct)
    {
        var asset = await db.VideoAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId && x.VideoId == videoId, ct)
            ?? throw Missing("asset");
        if (asset.Status != "ready" || asset.SizeBytes <= 0 || asset.DurationMs <= 0 || asset.ContentType != "video/mp4")
            throw new ApiException(503, "video_unavailable", "The video file is not ready for playback.");
        var expiresAt = clock.GetUtcNow().AddMinutes(10);
        var url = await signer.CreateReadUrlAsync(asset, expiresAt, ct);
        return new(url, expiresAt);
    }

    private static async Task<AssetView> UpdateAsset(Guid videoId, Guid assetId, AssetInput input, VideosDbContext db, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        input = AdminValidation.Asset(input);
        CheckContainer(input.BlobContainer, config);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var video = await LockVideo(db, videoId, ct);
        RequireSharedAsset(video);
        var asset = await db.VideoAssets.SingleOrDefaultAsync(x => x.Id == assetId && x.VideoId == videoId, ct) ?? throw Missing("asset");
        var locatorChanged = asset.BlobContainer != input.BlobContainer || asset.BlobName != input.BlobName || asset.Language != input.Language || asset.Rendition != input.Rendition;
        if (locatorChanged && await db.PlaybackSessions.AnyAsync(x => x.VideoAssetId == assetId, ct))
            throw new ApiException(409, "asset_in_use", "El asset tiene sesiones emitidas. Registra otro asset para cambiar blob, idioma o rendition.");
        Apply(asset, input); video.UpdatedAt = clock.GetUtcNow();
        await Save(db, ct); await tx.CommitAsync(ct);
        return View(asset);
    }

    private static void CheckContainer(string container, IConfiguration config)
    {
        var allowed = config.GetSection("Storage:AllowedContainers").Get<string[]>() ?? ["videos"];
        if (!allowed.Contains(container, StringComparer.Ordinal)) throw Invalid("blobContainer no está permitido en Storage:AllowedContainers.");
    }

    private static void Apply(VideoAsset asset, AssetInput input)
    {
        asset.Language = input.Language; asset.Rendition = input.Rendition; asset.BlobContainer = input.BlobContainer; asset.BlobName = input.BlobName;
        asset.ContentType = input.ContentType; asset.SizeBytes = input.SizeBytes; asset.DurationMs = input.DurationMs; asset.Status = input.Status;
    }

    private static async Task<AdminPage<VideoView>> VideoPage(IQueryable<Video> query, int page, int size, CancellationToken ct) => new(await query.OrderBy(x => x.ExternalExerciseId).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).Select(x => new VideoView(x.Id, x.ExternalExerciseId, x.Title, x.Status, x.CreatedAt, x.UpdatedAt, x.OwnerPartnerId, x.PartnerReference)).ToListAsync(ct), page, size, await query.CountAsync(ct));
    private static async Task<PartnerEntity> FindPartner(VideosDbContext db, Guid id, CancellationToken ct) => await db.Partners.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing("partner");
    private static async Task<PartnerEntity> LockPartner(VideosDbContext db, Guid id, CancellationToken ct) => await db.Partners.FromSqlInterpolated($"SELECT * FROM videos.partners WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw Missing("partner");
    private static async Task<Video> LockVideo(VideosDbContext db, Guid id, CancellationToken ct) => await db.Videos.FromSqlInterpolated($"SELECT * FROM videos.videos WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw Missing("video");
    private static async Task<License> FindLicense(VideosDbContext db, Guid partnerId, Guid id, CancellationToken ct) => await db.Licenses.SingleOrDefaultAsync(x => x.Id == id && x.PartnerId == partnerId, ct) ?? throw Missing("license");
    private static ApiException Missing(string resource) => new(404, "not_found", $"No se ha encontrado {resource}.");
    private static ApiException Invalid(string message) => new(400, "invalid_request", message);
    private static async Task Save(VideosDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new ApiException(409, "resource_conflict", "Ya existe un recurso con el mismo identificador o combinación única."); }
    }
    private static void RequireSharedAsset(Video video)
    {
        if (video.OwnerPartnerId != null)
            throw new ApiException(409, "private_upload_asset_managed", "Private upload assets must use the quota-aware upload workflow.");
    }

    private static void ApplyLicense(LicenseInput input, License license, PartnerEntity partner)
    {
        if (partner.ParentPartnerId != null && (input.AllowSubpartnerManagement == true || input.AllowCatalogueDelegation == true))
            throw Invalid("Subpartners cannot manage or delegate to another level.");
        PartnerDelegationEndpoints.ApplyLicense(input, license);
    }

    private static PartnerView View(PartnerEntity x) => PartnerDelegationEndpoints.View(x);
    private static CredentialView View(PartnerCredential x) => new(x.Id, x.KeyId, x.CreatedAt, x.ExpiresAt, x.RevokedAt);
    private static LicenseView View(License x) => PartnerDelegationEndpoints.LicenseViewOf(x);
    private static VideoView View(Video x) => new(x.Id, x.ExternalExerciseId, x.Title, x.Status, x.CreatedAt, x.UpdatedAt, x.OwnerPartnerId, x.PartnerReference);
    private static AssetView View(VideoAsset x) => new(x.Id, x.VideoId, x.Language, x.Rendition, x.BlobContainer, x.BlobName, x.ContentType, x.SizeBytes, x.DurationMs, x.Status, x.CreatedAt);
}

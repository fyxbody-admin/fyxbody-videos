using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FyxBody.Videos.Api.Partner;

public sealed class PartnerVideoService(VideosDbContext db, IVideoUrlSigner signer, TimeProvider clock, IConfiguration configuration)
{
    private DateTimeOffset Now => Microseconds(clock.GetUtcNow());
    private static DateTimeOffset Microseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
    public async Task<CatalogResponse> CatalogAsync(PartnerIdentity identity, int page, int pageSize, CancellationToken ct, string? externalExerciseId = null)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue)
            throw Error(400, "invalid_pagination", "Page must be positive and pageSize between 1 and 100.");
        await ValidateIdentityAsync(identity, ct);
        var query = CatalogueAccess.VisibleVideos(db, identity.PartnerId, Now);
        if (externalExerciseId is not null)
            query = query.Where(video => video.ExternalExerciseId == externalExerciseId);
        var total = await query.CountAsync(ct);
        var videos = await query.OrderBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return new(page, pageSize, total, await MapVideosAsync(videos, identity.PartnerId, ct));
    }

    public async Task<PartnerManagementView> ManagementAsync(PartnerIdentity identity, CancellationToken ct)
    {
        await ValidateIdentityAsync(identity, ct);
        var partner = await db.Partners.AsNoTracking().SingleAsync(x => x.Id == identity.PartnerId, ct);
        var licenses = await db.Licenses.AsNoTracking().Where(x => x.PartnerId == identity.PartnerId)
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToArrayAsync(ct);
        var now = Now;
        var active = licenses.Where(x => x.Status == "active" && x.StartsAt <= now && x.EndsAt > now).ToArray();
        return new(partner.ParentPartnerId == null && active.Any(x => x.AllowSubpartnerManagement),
            partner.ParentPartnerId == null && active.Any(x => x.AllowSubpartnerManagement && x.AllowPartnerUploads),
            licenses.Select(PartnerDelegationEndpoints.LicenseViewOf).ToArray());
    }

    public async Task<CatalogResponse> DelegableVideosAsync(PartnerIdentity identity, Guid licenseId, int? page, int? pageSize, CancellationToken ct)
    {
        await ValidateIdentityAsync(identity, ct);
        var (number, size) = Admin.AdminValidation.Pagination(page, pageSize);
        var source = CatalogueAccess.ActiveLicenses(db, identity.PartnerId, Now);
        if (!await db.Partners.AsNoTracking().AnyAsync(x => x.Id == identity.PartnerId && x.ParentPartnerId == null, ct)
            || !await source.AnyAsync(x => x.AllowSubpartnerManagement, ct)
            || !await source.AnyAsync(x => x.Id == licenseId && x.AllowCatalogueDelegation, ct))
            throw Error(403, "catalogue_delegation_forbidden", "The source license does not allow catalogue delegation.");
        var query = CatalogueAccess.DelegableVideos(db, identity.PartnerId, licenseId);
        var total = await query.CountAsync(ct);
        var videos = await query.OrderBy(x => x.Id).Skip((number - 1) * size).Take(size).ToArrayAsync(ct);
        return new(number, size, total, await MapVideosAsync(videos, identity.PartnerId, ct));
    }

    public async Task<VideoResponse> VideoAsync(PartnerIdentity identity, Guid videoId, CancellationToken ct)
    {
        await ValidateIdentityAsync(identity, ct);
        var video = await CatalogueAccess.VisibleVideos(db, identity.PartnerId, Now).SingleOrDefaultAsync(x => x.Id == videoId, ct)
            ?? throw Error(404, "video_not_found", "Video is unavailable.");
        return (await MapVideosAsync([video], identity.PartnerId, ct))[0];
    }

    private async Task<VideoResponse[]> MapVideosAsync(Video[] videos, Guid partnerId, CancellationToken ct)
    {
        var ids = videos.Select(x => x.Id).ToArray();
        var assets = await db.VideoAssets.AsNoTracking().Where(x => ids.Contains(x.VideoId) && x.Status == "ready" && x.SizeBytes > 0 && x.DurationMs > 0)
            .OrderBy(x => x.Language).ThenBy(x => x.Rendition).ToArrayAsync(ct);
        return videos.Select(v => new VideoResponse(v.Id, v.ExternalExerciseId, v.Title, assets.Where(a => a.VideoId == v.Id)
            .Select(a => new AssetResponse(a.Id, a.Language, a.Rendition, a.ContentType, a.SizeBytes, a.DurationMs)).ToArray(), v.OwnerPartnerId == partnerId ? v.PartnerReference : null)).ToArray();
    }

    public Task<PlaybackSessionResponse> IssueAsync(PartnerIdentity identity, Guid videoId, Guid assetId, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl))
            throw Error(400, "invalid_idempotency_key", "A nonempty Idempotency-Key of at most 128 characters is required.");
        return RetryAsync(() => IssueOnceAsync(identity, videoId, assetId, key, ct), ct);
    }

    private async Task<PlaybackSessionResponse> IssueOnceAsync(PartnerIdentity identity, Guid videoId, Guid assetId, string key, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // Catalogue changes take the principal lock before the child lock.
        var parentId = await db.Partners.AsNoTracking().Where(partner => partner.Id == identity.PartnerId)
            .Select(partner => partner.ParentPartnerId).SingleOrDefaultAsync(ct);
        if (parentId is { } principalId)
            await db.Partners.FromSqlInterpolated($"SELECT * FROM videos.partners WHERE id = {principalId} FOR SHARE").AsNoTracking().ToArrayAsync(ct);
        await LockPartnerAsync(identity, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{videoId:D}|{assetId:D}")));
        var existing = await db.PlaybackSessions.AsNoTracking().SingleOrDefaultAsync(x => x.PartnerId == identity.PartnerId && x.IdempotencyKey == key, ct);
        if (existing != null && existing.RequestHash != hash) throw Error(409, "idempotency_conflict", "The key was already used with another request.");
        var lockedVideo = await db.Videos.FromSqlInterpolated($"SELECT * FROM videos.videos WHERE id = {videoId} FOR SHARE").AsNoTracking().ToArrayAsync(ct);
        if (lockedVideo.Length == 0 || lockedVideo[0].Status != "active"
            || (lockedVideo[0].OwnerPartnerId != null && lockedVideo[0].OwnerPartnerId != identity.PartnerId
                && !await CatalogueAccess.EffectiveGrants(db, identity.PartnerId, Now).AnyAsync(grant => grant.VideoId == videoId, ct))) throw Error(404, "video_not_found", "Video is unavailable.");
        await ValidateIdentityAsync(identity, ct);
        var now = Now;
        var asset = await db.VideoAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId && x.VideoId == videoId && x.Status == "ready" && x.SizeBytes > 0 && x.DurationMs > 0, ct)
            ?? throw Error(404, "asset_not_found", "Asset is unavailable.");
        if (existing != null)
        {
            if (existing.Status != "issued" || existing.ExpiresAt <= now) throw Error(409, "session_expired", "Use a new key to request another session.");
            var replayLicense = await GrantedTerms(identity.PartnerId, videoId, now).SingleOrDefaultAsync(l => l.Id == existing.LicenseId, ct);
            if (replayLicense == null)
                throw Error(403, "license_unavailable", "The license is unavailable.");
            if (replayLicense.ExpiresAt < existing.ExpiresAt)
                throw Error(409, "session_license_changed", "The license term changed; use a new idempotency key.");
            var replayUrl = await signer.CreateReadUrlAsync(asset, existing.ExpiresAt, ct);
            await tx.CommitAsync(ct);
            return new(existing.Id, videoId, assetId, existing.ExpiresAt, replayUrl, true);
        }
        var month = new DateOnly(now.Year, now.Month, 1);
        var licenses = await GrantedTerms(identity.PartnerId, videoId, now).OrderByDescending(x => x.UnlimitedSessions).ThenBy(x => x.ExpiresAt).ThenBy(x => x.Id).ToArrayAsync(ct);
        if (licenses.Length == 0) throw Error(403, "license_unavailable", "No valid license covers this video.");
        GrantedTerm? chosen = null;
        foreach (var license in licenses)
        {
            if (!license.UnlimitedSessions && license.MonthlySessionQuota == 0) continue;
            var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO videos.usage_monthly (partner_id, license_id, month, sessions_issued, updated_at)
                VALUES ({identity.PartnerId}, {license.Id}, {month}, 1, {now})
                ON CONFLICT (partner_id, license_id, month) DO UPDATE
                SET sessions_issued = videos.usage_monthly.sessions_issued + 1, updated_at = EXCLUDED.updated_at
                WHERE {license.UnlimitedSessions} OR videos.usage_monthly.sessions_issued < {license.MonthlySessionQuota}
                """, ct);
            if (changed == 1) { chosen = license; break; }
        }
        if (chosen == null) throw Error(429, "monthly_quota_exceeded", "The monthly session quota has been reached.");
        var lifetime = configuration.GetValue<int?>("Playback:SessionLifetimeSeconds") ?? 300;
        if (lifetime is < 30 or > 900) throw Error(503, "playback_configuration", "Playback lifetime is invalid.");
        var expiry = now.AddSeconds(lifetime);
        if (expiry > chosen.ExpiresAt) expiry = chosen.ExpiresAt;
        var session = new PlaybackSession { PartnerId = identity.PartnerId, LicenseId = chosen.Id, VideoId = videoId,
            VideoAssetId = assetId, IdempotencyKey = key, RequestHash = hash, CreatedAt = now, ExpiresAt = expiry, UsageMonth = month };
        db.PlaybackSessions.Add(session);
        await db.SaveChangesAsync(ct);
        var url = await signer.CreateReadUrlAsync(asset, expiry, ct);
        await tx.CommitAsync(ct);
        return new(session.Id, videoId, assetId, expiry, url, false);
    }

    private sealed class GrantedTerm
    {
        public Guid Id { get; init; }
        public bool UnlimitedSessions { get; init; }
        public long MonthlySessionQuota { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private IQueryable<GrantedTerm> GrantedTerms(Guid partnerId, Guid videoId, DateTimeOffset now) =>
        from grant in CatalogueAccess.EffectiveGrants(db, partnerId, now)
        join license in db.Licenses.AsNoTracking() on grant.LicenseId equals license.Id
        join upstream in db.Licenses.AsNoTracking() on grant.SourceLicenseId equals (Guid?)upstream.Id into sources
        from source in sources.DefaultIfEmpty()
        where grant.VideoId == videoId
        select new GrantedTerm
        {
            Id = license.Id,
            UnlimitedSessions = license.UnlimitedSessions,
            MonthlySessionQuota = license.MonthlySessionQuota,
            ExpiresAt = source != null && source.EndsAt < license.EndsAt ? source.EndsAt : license.EndsAt
        };

    public Task<EventBatchResponse> EventsAsync(PartnerIdentity identity, PlaybackEventRequest[]? events, CancellationToken ct)
    {
        if (events == null || events.Length is < 1 or > 50) throw Error(400, "invalid_events", "Submit between 1 and 50 events.");
        return RetryAsync(() => EventsOnceAsync(identity, events, ct), ct);
    }

    private async Task<EventBatchResponse> EventsOnceAsync(PartnerIdentity identity, PlaybackEventRequest[] events, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockPartnerAsync(identity, ct);
        var now = Now;
        var accepted = 0;
        var duplicates = 0;
        foreach (var input in events)
        {
            if (input == null || string.IsNullOrWhiteSpace(input.EventKey) || input.EventKey.Length > 128 || input.EventKey.Any(char.IsControl)
                || input.Kind is not ("started" or "progress" or "completed" or "failed") || input.PositionMs < 0)
                throw Error(400, "invalid_event", "Invalid event key, kind or position.");
            var session = await db.PlaybackSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == input.SessionId && s.PartnerId == identity.PartnerId, ct)
                ?? throw Error(404, "session_not_found", "Session is unavailable.");
            var occurrence = Microseconds(input.OccurredAt);
            if (occurrence < session.CreatedAt || occurrence > session.ExpiresAt || occurrence > now.AddSeconds(30) || now > session.ExpiresAt.AddHours(24))
                throw Error(400, "invalid_event_time", "The event is outside the permitted session or receipt window.");
            var duration = await db.VideoAssets.Where(a => a.Id == session.VideoAssetId).Select(a => a.DurationMs).SingleAsync(ct);
            if (input.PositionMs > duration) throw Error(400, "invalid_event_position", "Position exceeds video duration.");
            var existing = db.PlaybackEvents.Local.FirstOrDefault(e => e.PlaybackSessionId == input.SessionId && e.EventKey == input.EventKey)
                ?? await db.PlaybackEvents.SingleOrDefaultAsync(e => e.PlaybackSessionId == input.SessionId && e.EventKey == input.EventKey, ct);
            if (existing != null)
            {
                if (existing.Kind != input.Kind || existing.PositionMs != input.PositionMs || existing.OccurredAt != occurrence)
                    throw Error(409, "event_conflict", "The event key was already used with another payload.");
                duplicates++; continue;
            }
            db.PlaybackEvents.Add(new PlaybackEvent { PlaybackSessionId = input.SessionId, EventKey = input.EventKey, Kind = input.Kind,
                PositionMs = input.PositionMs, OccurredAt = occurrence, CreatedAt = now });
            accepted++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(accepted, duplicates);
    }

    public async Task<UsageResponse> UsageAsync(PartnerIdentity identity, string? monthText, CancellationToken ct)
    {
        await ValidateIdentityAsync(identity, ct);
        var now = Now;
        monthText ??= $"{now.Year:0000}-{now.Month:00}";
        if (!DateOnly.TryParseExact(monthText + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) || month.Year >= 9999)
            throw Error(400, "invalid_month", "Month must use YYYY-MM.");
        var from = new DateTimeOffset(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddMonths(1);
        var licenses = await db.Licenses.AsNoTracking().Where(l => l.PartnerId == identity.PartnerId).OrderBy(l => l.Id).ToArrayAsync(ct);
        var counters = await db.UsageMonthly.AsNoTracking().Where(x => x.PartnerId == identity.PartnerId && x.Month == month).ToArrayAsync(ct);
        var events = db.PlaybackEvents.AsNoTracking().Where(e => e.OccurredAt >= from && e.OccurredAt < to
            && db.PlaybackSessions.Any(s => s.Id == e.PlaybackSessionId && s.PartnerId == identity.PartnerId));
        return new(monthText, licenses.Select(l => new LicenseUsageResponse(l.Id, l.MonthlySessionQuota, counters.SingleOrDefault(c => c.LicenseId == l.Id)?.SessionsIssued ?? 0, l.Name, l.UnlimitedSessions)).ToArray(),
            await events.Where(e => e.Kind == "started").Select(e => e.PlaybackSessionId).Distinct().CountAsync(ct),
            await events.Where(e => e.Kind == "completed").Select(e => e.PlaybackSessionId).Distinct().CountAsync(ct));
    }

    private async Task LockPartnerAsync(PartnerIdentity identity, CancellationToken ct)
    {
        await db.Partners.FromSqlInterpolated($"SELECT * FROM videos.partners WHERE id = {identity.PartnerId} FOR UPDATE").AsNoTracking().ToArrayAsync(ct);
        await ValidateIdentityAsync(identity, ct);
    }

    internal async Task ValidateIdentityAsync(PartnerIdentity identity, CancellationToken ct)
    {
        var now = Now;
        var partnerActive = await db.Partners.AsNoTracking().AnyAsync(x => x.Id == identity.PartnerId && x.Status == "active", ct);
        var identityActive = identity.PortalUserId is { } portalUserId
            ? await db.PortalUsers.AsNoTracking().AnyAsync(x => x.Id == portalUserId && x.SecurityStamp == identity.PortalSecurityStamp && x.Role == "partner" && x.PartnerId == identity.PartnerId && x.IsActive && !x.MustChangePassword, ct)
            : await db.PartnerCredentials.AsNoTracking().AnyAsync(x => x.Id == identity.CredentialId && x.PartnerId == identity.PartnerId
                && x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now), ct);
        if (!partnerActive || !identityActive)
            throw Error(401, "partner_unauthorized", "Partner access is unavailable.");
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await action(); }
            catch (Exception ex) when (SqlState(ex) is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                db.ChangeTracker.Clear();
                if (attempt >= 2) throw Error(503, "concurrent_operation", "Please retry the operation.");
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct);
            }
            catch (DbUpdateException ex) when (SqlState(ex) == PostgresErrorCodes.UniqueViolation)
            {
                db.ChangeTracker.Clear();
                throw Error(409, "concurrent_conflict", "A conflicting request was processed; retry with the same key.");
            }
            catch { db.ChangeTracker.Clear(); throw; }
        }
    }

    private static string? SqlState(Exception ex) => ex is PostgresException pg ? pg.SqlState : ex.InnerException is { } inner ? SqlState(inner) : null;
    private static ApiException Error(int status, string code, string message) => new(status, code, message);
}

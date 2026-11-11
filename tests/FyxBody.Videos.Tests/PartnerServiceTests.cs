using FyxBody.Videos.Api;
using FyxBody.Videos.Api.Partner;
using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using DataPartner = FyxBody.Videos.Data.Partner;

namespace FyxBody.Videos.Tests;

public sealed class PartnerServiceTests
{
    private static VideosDbContext Context()
    {
        var value = Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")!;
        var connection = new NpgsqlConnectionStringBuilder(value);
        if (connection.Host is not ("127.0.0.1" or "localhost") || connection.Database != "fyxbody_videos") throw new InvalidOperationException("Local disposable database required.");
        return new(new DbContextOptionsBuilder<VideosDbContext>().UseNpgsql(value, o => o.SetPostgresVersion(16, 0).MigrationsHistoryTable("__EFMigrationsHistory", "videos")).Options);
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class FakeSigner(bool fail = false) : IVideoUrlSigner
    {
        public Task<Uri> CreateReadUrlAsync(VideoAsset asset, DateTimeOffset expiresAt, CancellationToken ct) => fail
            ? throw new ApiException(503, "fake_unavailable", "Unavailable") : Task.FromResult(new Uri("https://local.invalid/video"));
    }
    private sealed record Seed(PartnerIdentity Identity, Guid Video, Guid Asset, Guid License, DateTimeOffset Now);
    private static PartnerVideoService Service(VideosDbContext db, Seed seed, bool fail = false, DateTimeOffset? now = null) => new(db, new FakeSigner(fail), new FixedClock(now ?? seed.Now), new ConfigurationBuilder().Build());
    private static async Task<Seed> SeedAsync(long quota = 3)
    {
        await using var db = Context();
        var now = DateTimeOffset.UtcNow;
        now = new(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
        var partner = new DataPartner { Code = Guid.NewGuid().ToString("N"), Name = "Integration" };
        var credential = new PartnerCredential { PartnerId = partner.Id, KeyId = Guid.NewGuid().ToString("N"), SecretHash = new string('h', 64) };
        var video = new Video { ExternalExerciseId = Guid.NewGuid().ToString("N"), Title = "Integration", Status = "active" };
        var asset = new VideoAsset { VideoId = video.Id, BlobContainer = "test", BlobName = Guid.NewGuid().ToString("N"), Status = "ready", SizeBytes = 100, DurationMs = 20000 };
        var license = new License { PartnerId = partner.Id, Name = "Test", StartsAt = now.AddDays(-1), EndsAt = now.AddDays(1), MonthlySessionQuota = quota };
        db.AddRange(partner, credential, video, asset, license, new LicenseVideo { LicenseId = license.Id, VideoId = video.Id, CreatedAt = now });
        await db.SaveChangesAsync();
        return new(new(partner.Id, credential.Id), video.Id, asset.Id, license.Id, now);
    }

    [PostgresFact]
    public async Task CatalogIsUniqueAcrossOverlappingLicensesAndDoesNotExposeStorage()
    {
        var seed = await SeedAsync();
        await using var db = Context();
        var other = new License { PartnerId = seed.Identity.PartnerId, Name = "Overlap", StartsAt = seed.Now.AddHours(-1), EndsAt = seed.Now.AddHours(2), MonthlySessionQuota = 10 };
        db.AddRange(other, new LicenseVideo { LicenseId = other.Id, VideoId = seed.Video });
        await db.SaveChangesAsync();
        var catalog = await Service(db, seed).CatalogAsync(seed.Identity, 1, 25, default);
        Assert.Equal(1, catalog.Total);
        Assert.Single(catalog.Items);
        Assert.Single(catalog.Items[0].Assets);
        Assert.Equal(seed.Asset, catalog.Items[0].Assets[0].Id);
        var json = System.Text.Json.JsonSerializer.Serialize(catalog);
        Assert.DoesNotContain("Blob", json);
    }

    [PostgresFact]
    public async Task ConcurrentRequestsCannotExceedQuotaAndReplayDoesNotCharge()
    {
        var seed = await SeedAsync();
        var tasks = Enumerable.Range(0, 10).Select(async i => {
            await using var db = Context();
            try { await Service(db, seed).IssueAsync(seed.Identity, seed.Video, seed.Asset, "key-" + i, default); return true; }
            catch (ApiException ex) when (ex.Code == "monthly_quota_exceeded") { return false; }
        });
        Assert.Equal(3, (await Task.WhenAll(tasks)).Count(x => x));
        await using var verify = Context();
        var session = await verify.PlaybackSessions.FirstAsync(s => s.PartnerId == seed.Identity.PartnerId);
        var replay = await Service(verify, seed).IssueAsync(seed.Identity, seed.Video, seed.Asset, session.IdempotencyKey, default);
        Assert.True(replay.Replayed);
        Assert.Equal(session.Id, replay.SessionId);
        Assert.Equal(session.ExpiresAt, replay.ExpiresAt);
        Assert.Equal(3, await verify.PlaybackSessions.CountAsync(s => s.PartnerId == seed.Identity.PartnerId));
        Assert.Equal(3, (await verify.UsageMonthly.SingleAsync(u => u.PartnerId == seed.Identity.PartnerId)).SessionsIssued);
    }

    [PostgresFact]
    public async Task ConcurrentSameKeyCreatesExactlyOneSession()
    {
        var seed = await SeedAsync();
        var tasks = Enumerable.Range(0, 6).Select(async i => { await using var db = Context(); return await Service(db, seed).IssueAsync(seed.Identity, seed.Video, seed.Asset, "same-key", default); });
        var results = await Task.WhenAll(tasks);
        Assert.Single(results.Select(x => x.SessionId).Distinct());
        Assert.Single(results, x => !x.Replayed);
        await using var verify = Context();
        Assert.Equal(1, (await verify.UsageMonthly.SingleAsync(u => u.PartnerId == seed.Identity.PartnerId)).SessionsIssued);
    }

    [PostgresFact]
    public async Task SignerFailureRollsBackSessionAndCounter()
    {
        var seed = await SeedAsync();
        await using (var db = Context())
            await Assert.ThrowsAsync<ApiException>(() => Service(db, seed, fail: true).IssueAsync(seed.Identity, seed.Video, seed.Asset, "failure", default));
        await using var verify = Context();
        Assert.False(await verify.PlaybackSessions.AnyAsync(s => s.PartnerId == seed.Identity.PartnerId));
        Assert.False(await verify.UsageMonthly.AnyAsync(s => s.PartnerId == seed.Identity.PartnerId));
    }

    [PostgresFact]
    public async Task RevokedGrantBlocksReplayAndPayloadConflictIsRejected()
    {
        var seed = await SeedAsync();
        await using var db = Context();
        var service = Service(db, seed);
        await service.IssueAsync(seed.Identity, seed.Video, seed.Asset, "key", default);
        var conflict = await Assert.ThrowsAsync<ApiException>(() => service.IssueAsync(seed.Identity, seed.Video, Guid.NewGuid(), "key", default));
        Assert.Equal(409, conflict.Status);
        await db.LicenseVideos.Where(g => g.LicenseId == seed.License).ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, seed.Now));
        var revoked = await Assert.ThrowsAsync<ApiException>(() => service.IssueAsync(seed.Identity, seed.Video, seed.Asset, "key", default));
        Assert.Equal("license_unavailable", revoked.Code);
    }

    [PostgresFact]
    public async Task EventsDeduplicateAndConflictRollsBackEntireBatch()
    {
        var seed = await SeedAsync();
        await using var db = Context();
        var service = Service(db, seed);
        var session = await service.IssueAsync(seed.Identity, seed.Video, seed.Asset, "events", default);
        var start = new PlaybackEventRequest(session.SessionId, "start", "started", 0, seed.Now);
        var result = await service.EventsAsync(seed.Identity, [start, start], default);
        Assert.Equal(new EventBatchResponse(1, 1), result);
        var completed = new PlaybackEventRequest(session.SessionId, "completed", "completed", 20000, seed.Now);
        var conflict = await Assert.ThrowsAsync<ApiException>(() => service.EventsAsync(seed.Identity, [completed, start with { PositionMs = 1 }], default));
        Assert.Equal(409, conflict.Status);
        await using var verify = Context();
        Assert.Equal(1, await verify.PlaybackEvents.CountAsync(e => e.PlaybackSessionId == session.SessionId));
        var usage = await Service(verify, seed).UsageAsync(seed.Identity, null, default);
        Assert.Equal(1, usage.StartedSessions);
        Assert.Equal(0, usage.CompletedSessions);
    }

    [PostgresFact]
    public async Task EventsCanArriveAfterRevocationButNotForAnotherPartnersSession()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        await using var db = Context();
        var session = await Service(db, seed).IssueAsync(seed.Identity, seed.Video, seed.Asset, "events", default);
        await db.Licenses.Where(l => l.Id == seed.License).ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "revoked"));
        var ev = new PlaybackEventRequest(session.SessionId, "late", "started", 0, seed.Now);
        Assert.Equal(1, (await Service(db, seed, now: session.ExpiresAt.AddHours(1)).EventsAsync(seed.Identity, [ev], default)).Accepted);
        var forbidden = await Assert.ThrowsAsync<ApiException>(() => Service(db, other).EventsAsync(other.Identity, [ev], default));
        Assert.Equal(404, forbidden.Status);
    }

    private sealed class CountingSigner : IVideoUrlSigner
    {
        public int Calls { get; private set; }
        public Task<Uri> CreateReadUrlAsync(VideoAsset asset, DateTimeOffset expiresAt, CancellationToken ct)
        { Calls++; return Task.FromResult(new Uri("https://local.invalid/video")); }
    }

    [PostgresFact]
    public async Task ShortenedLicenseRejectsReplayWithoutSigningOrChargingAgain()
    {
        var seed = await SeedAsync();
        await using var db = Context();
        var signer = new CountingSigner();
        var service = new PartnerVideoService(db, signer, new FixedClock(seed.Now), new ConfigurationBuilder().Build());
        var session = await service.IssueAsync(seed.Identity, seed.Video, seed.Asset, "shortened", default);
        await db.Licenses.Where(l => l.Id == seed.License).ExecuteUpdateAsync(s => s.SetProperty(l => l.EndsAt, seed.Now.AddSeconds(60)));
        var failure = await Assert.ThrowsAsync<ApiException>(() => service.IssueAsync(seed.Identity, seed.Video, seed.Asset, "shortened", default));
        Assert.Equal(409, failure.Status);
        Assert.Equal("session_license_changed", failure.Code);
        Assert.Equal(1, signer.Calls);
        Assert.Equal(1, (await db.UsageMonthly.AsNoTracking().SingleAsync(u => u.PartnerId == seed.Identity.PartnerId)).SessionsIssued);
        Assert.Equal(session.ExpiresAt, (await db.PlaybackSessions.AsNoTracking().SingleAsync(s => s.Id == session.SessionId)).ExpiresAt);
    }

    private sealed class BarrierSigner : IVideoUrlSigner
    {
        private int calls;
        public TaskCompletionSource BothEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Uri> CreateReadUrlAsync(VideoAsset asset, DateTimeOffset expiresAt, CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 2) BothEntered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new Uri("https://local.invalid/video");
        }
    }

    [PostgresFact]
    public async Task DifferentPartnersCanSignTheSameVideoConcurrently()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await using (var setup = Context())
        {
            setup.LicenseVideos.Add(new LicenseVideo { LicenseId = second.License, VideoId = first.Video });
            await setup.SaveChangesAsync();
        }
        var signer = new BarrierSigner();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var db1 = Context();
        await using var db2 = Context();
        var service1 = new PartnerVideoService(db1, signer, new FixedClock(first.Now), new ConfigurationBuilder().Build());
        var service2 = new PartnerVideoService(db2, signer, new FixedClock(second.Now), new ConfigurationBuilder().Build());
        var task1 = service1.IssueAsync(first.Identity, first.Video, first.Asset, "shared-first", timeout.Token);
        var task2 = service2.IssueAsync(second.Identity, first.Video, first.Asset, "shared-second", timeout.Token);
        try { await signer.BothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally
        {
            signer.Release.TrySetResult();
            await Task.WhenAll(task1, task2);
        }
    }

    [PostgresFact]
    public async Task SevenDigitTimestampsRemainIdempotentAcrossContexts()
    {
        var seed = await SeedAsync();
        var preciseNow = seed.Now.AddTicks(7);
        PlaybackSessionResponse session;
        await using (var first = Context())
            session = await Service(first, seed, now: preciseNow).IssueAsync(seed.Identity, seed.Video, seed.Asset, "precision", default);
        Assert.Equal(0, session.ExpiresAt.Ticks % 10);
        var input = new PlaybackEventRequest(session.SessionId, "precise", "started", 0, preciseNow);
        await using (var first = Context())
            Assert.Equal(new EventBatchResponse(1, 0), await Service(first, seed, now: preciseNow).EventsAsync(seed.Identity, [input], default));
        await using (var second = Context())
        {
            var service = Service(second, seed, now: preciseNow);
            Assert.Equal(new EventBatchResponse(0, 1), await service.EventsAsync(seed.Identity, [input], default));
            var replay = await service.IssueAsync(seed.Identity, seed.Video, seed.Asset, "precision", default);
            Assert.Equal(session.ExpiresAt, replay.ExpiresAt);
        }
    }

    [PostgresFact]
    public async Task ExpiredReplaysAndLateEventsAreRejected()
    {
        var seed = await SeedAsync();
        await using var db = Context();
        var session = await Service(db, seed).IssueAsync(seed.Identity, seed.Video, seed.Asset, "expired", default);
        var expired = await Assert.ThrowsAsync<ApiException>(() => Service(db, seed, now: session.ExpiresAt.AddSeconds(1)).IssueAsync(seed.Identity, seed.Video, seed.Asset, "expired", default));
        Assert.Equal(409, expired.Status);
        var ev = new PlaybackEventRequest(session.SessionId, "too-late", "started", 0, seed.Now);
        var late = await Assert.ThrowsAsync<ApiException>(() => Service(db, seed, now: session.ExpiresAt.AddHours(25)).EventsAsync(seed.Identity, [ev], default));
        Assert.Equal(400, late.Status);
    }
}

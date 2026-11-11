using System.Net;
using System.Net.Http.Json;
using FyxBody.Videos.Api;
using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Api.Partner;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using DataPartner = FyxBody.Videos.Data.Partner;

namespace FyxBody.Videos.Tests;

public sealed class UnlimitedLicenseTests
{
    private static string Connection()
    {
        var value = Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")!; var settings = new NpgsqlConnectionStringBuilder(value);
        if (settings.Host is not ("localhost" or "127.0.0.1") || settings.Database != "fyxbody_videos") throw new InvalidOperationException("A disposable local database is required."); return value;
    }
    private static VideosDbContext Db() => new(new DbContextOptionsBuilder<VideosDbContext>().UseNpgsql(Connection(), options => options.SetPostgresVersion(16, 0).MigrationsHistoryTable("__EFMigrationsHistory", "videos")).Options);
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class Signer(bool fail = false) : IVideoUrlSigner
    {
        public Task<Uri> CreateReadUrlAsync(VideoAsset asset, DateTimeOffset expiresAt, CancellationToken ct) => fail
            ? throw new ApiException(503, "video_storage_unavailable", "Test storage failure.") : Task.FromResult(new Uri("https://local.invalid/test.mp4"));
    }
    private sealed record Seed(PartnerIdentity Identity, Guid VideoId, Guid AssetId, Guid LicenseId, DateTimeOffset Now, string ApiKey);
    private static PartnerVideoService Service(VideosDbContext db, Seed seed, bool fail = false) => new(db, new Signer(fail), new Clock(seed.Now), new ConfigurationBuilder().Build());
    private static async Task<Seed> SeedAsync(bool unlimited = true, long quota = 0)
    {
        await using var db = Db(); var current = DateTimeOffset.UtcNow; var now = new DateTimeOffset(current.Ticks - current.Ticks % 10, TimeSpan.Zero);
        var partner = new DataPartner { Code = Guid.NewGuid().ToString("N"), Name = "Unlimited integration test" };
        var (key, secret, hash) = CredentialSecrets.Generate();
        var credential = new PartnerCredential { PartnerId = partner.Id, KeyId = key, SecretHash = hash, CreatedAt = now };
        var video = new Video { ExternalExerciseId = Guid.NewGuid().ToString("N"), Title = "Unlimited integration test", Status = "active" };
        var asset = new VideoAsset { VideoId = video.Id, BlobContainer = "test", BlobName = Guid.NewGuid() + ".mp4", SizeBytes = 100, DurationMs = 1000, Status = "ready" };
        var license = new License { PartnerId = partner.Id, Name = "Unlimited integration test", StartsAt = now.AddDays(-1), EndsAt = now.AddDays(1), MonthlySessionQuota = quota, UnlimitedSessions = unlimited };
        db.AddRange(partner, credential, video, asset, license, new LicenseVideo { LicenseId = license.Id, VideoId = video.Id, CreatedAt = now }); await db.SaveChangesAsync();
        return new(new(partner.Id, credential.Id), video.Id, asset.Id, license.Id, now, key + "." + secret);
    }
    private static WebApplicationFactory<Program> App() => AdminValidationTests.CreateApplication(Connection()).WithWebHostBuilder(builder => {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["VideoImports:Enabled"] = "false" }));
        builder.ConfigureServices(services => { services.RemoveAll<IVideoUrlSigner>(); services.AddSingleton<IVideoUrlSigner>(new Signer()); });
    });

    [Fact]
    public void UnlimitedNormalizesNonnegativeQuotaAndUsesNoSentinel()
    {
        var now = DateTimeOffset.UtcNow; var input = new LicenseInput("Test", "active", now, now.AddDays(1), 123, true);
        Assert.Equal(0, AdminValidation.License(input).MonthlySessionQuota);
        Assert.Throws<ApiException>(() => AdminValidation.License(input with { MonthlySessionQuota = -1 }));
        Assert.Null(new LicenseInput("Legacy", "active", now, now.AddDays(1), 3).UnlimitedSessions);
    }

    [PostgresFact]
    public async Task LimitedZeroAndExhaustedLicensesStillBlockNewSessions()
    {
        foreach (var quota in new[] { 0, 2 })
        {
            var seed = await SeedAsync(unlimited: false, quota: quota); await using var db = Db(); var service = Service(db, seed);
            for (var index = 0; index < quota; index++) await service.IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "limited-" + index, default);
            var denied = await Assert.ThrowsAsync<ApiException>(() => service.IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "limited-exhausted", default));
            Assert.Equal(429, denied.Status); Assert.Equal("monthly_quota_exceeded", denied.Code);
            Assert.Equal(quota, await db.PlaybackSessions.CountAsync(session => session.PartnerId == seed.Identity.PartnerId));
            var counter = await db.UsageMonthly.AsNoTracking().SingleOrDefaultAsync(usage => usage.PartnerId == seed.Identity.PartnerId);
            if (quota == 0) Assert.Null(counter); else Assert.Equal(quota, counter!.SessionsIssued);
        }
    }

    [PostgresFact]
    public async Task UnlimitedZeroQuotaCountsConcurrentUniqueSessionsAndOnlyOneConcurrentReplay()
    {
        var seed = await SeedAsync();
        var unique = Enumerable.Range(0, 20).Select(async index => { await using var db = Db(); return await Service(db, seed).IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "unlimited-" + index, default); });
        Assert.All(await Task.WhenAll(unique), result => Assert.False(result.Replayed));
        var same = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ => { await using var db = Db(); return await Service(db, seed).IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "unlimited-same", default); }));
        Assert.Single(same.Select(result => result.SessionId).Distinct()); Assert.Single(same, result => !result.Replayed);
        await using var verify = Db(); Assert.Equal(21, await verify.PlaybackSessions.CountAsync(session => session.PartnerId == seed.Identity.PartnerId));
        Assert.Equal(21, (await verify.UsageMonthly.SingleAsync(usage => usage.LicenseId == seed.LicenseId)).SessionsIssued);
        var usage = await Service(verify, seed).UsageAsync(seed.Identity, null, default); var item = Assert.Single(usage.Licenses);
        Assert.True(item.UnlimitedSessions); Assert.Equal(0, item.MonthlySessionQuota); Assert.Equal(21, item.SessionsIssued);
    }

    [PostgresFact]
    public async Task UnlimitedSigningFailureRollsBackBothNewAndExistingCounters()
    {
        var seed = await SeedAsync();
        await using (var failure = Db()) await Assert.ThrowsAsync<ApiException>(() => Service(failure, seed, fail: true).IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "failure-new", default));
        await using (var check = Db()) { Assert.False(await check.UsageMonthly.AnyAsync(usage => usage.LicenseId == seed.LicenseId)); Assert.False(await check.PlaybackSessions.AnyAsync(session => session.PartnerId == seed.Identity.PartnerId)); }
        await using (var first = Db()) await Service(first, seed).IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "success", default);
        await using (var failure = Db()) await Assert.ThrowsAsync<ApiException>(() => Service(failure, seed, fail: true).IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "failure-existing", default));
        await using var verify = Db(); Assert.Equal(1, (await verify.UsageMonthly.SingleAsync(usage => usage.LicenseId == seed.LicenseId)).SessionsIssued); Assert.Single(await verify.PlaybackSessions.Where(session => session.PartnerId == seed.Identity.PartnerId).ToArrayAsync());
    }

    [PostgresFact]
    public async Task UnlimitedDoesNotBypassLicensePartnerCredentialOrCatalogueRestrictions()
    {
        foreach (var kind in new[] { "expired", "suspended", "revoked", "grant", "partner", "credential", "video" })
        {
            var seed = await SeedAsync(); await using var db = Db();
            switch (kind)
            {
                case "expired": await db.Licenses.Where(license => license.Id == seed.LicenseId).ExecuteUpdateAsync(update => update.SetProperty(license => license.EndsAt, seed.Now.AddSeconds(-1))); break;
                case "suspended": case "revoked": await db.Licenses.Where(license => license.Id == seed.LicenseId).ExecuteUpdateAsync(update => update.SetProperty(license => license.Status, kind)); break;
                case "grant": await db.LicenseVideos.Where(grant => grant.LicenseId == seed.LicenseId).ExecuteUpdateAsync(update => update.SetProperty(grant => grant.RevokedAt, seed.Now)); break;
                case "partner": await db.Partners.Where(partner => partner.Id == seed.Identity.PartnerId).ExecuteUpdateAsync(update => update.SetProperty(partner => partner.Status, "suspended")); break;
                case "credential": await db.PartnerCredentials.Where(credential => credential.Id == seed.Identity.CredentialId).ExecuteUpdateAsync(update => update.SetProperty(credential => credential.RevokedAt, seed.Now)); break;
                case "video": await db.Videos.Where(video => video.Id == seed.VideoId).ExecuteUpdateAsync(update => update.SetProperty(video => video.Status, "archived")); break;
            }
            var rejected = await Assert.ThrowsAsync<ApiException>(() => Service(db, seed).IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "restriction", default));
            Assert.Equal(kind is "partner" or "credential" ? 401 : kind == "video" ? 404 : 403, rejected.Status);
            Assert.False(await db.UsageMonthly.AnyAsync(usage => usage.LicenseId == seed.LicenseId));
        }
        var owner = await SeedAsync(); var other = await SeedAsync(); await using var verify = Db();
        var forbidden = await Assert.ThrowsAsync<ApiException>(() => Service(verify, owner).IssueAsync(owner.Identity, other.VideoId, other.AssetId, "other-catalogue", default)); Assert.Equal(403, forbidden.Status);
        var ownUsage = await Service(verify, owner).UsageAsync(owner.Identity, null, default); Assert.Equal(owner.LicenseId, Assert.Single(ownUsage.Licenses).LicenseId);
    }

    [PostgresFact]
    public async Task UnlimitedOverlapIsPreferredWithoutReassigningAnExistingLimitedReplay()
    {
        var seed = await SeedAsync(unlimited: false, quota: 2); await using var db = Db(); var service = Service(db, seed);
        var limited = await service.IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "limited-original", default);
        var unlimited = new License { PartnerId = seed.Identity.PartnerId, Name = "Unlimited overlap", StartsAt = seed.Now.AddDays(-1), EndsAt = seed.Now.AddDays(2), UnlimitedSessions = true, MonthlySessionQuota = 0 };
        db.AddRange(unlimited, new LicenseVideo { LicenseId = unlimited.Id, VideoId = seed.VideoId }); await db.SaveChangesAsync();
        var replay = await service.IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "limited-original", default); Assert.True(replay.Replayed); Assert.Equal(limited.SessionId, replay.SessionId);
        var next = await service.IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "preferred-unlimited", default);
        Assert.Equal(unlimited.Id, (await db.PlaybackSessions.AsNoTracking().SingleAsync(session => session.Id == next.SessionId)).LicenseId);
        Assert.Equal(seed.LicenseId, (await db.PlaybackSessions.AsNoTracking().SingleAsync(session => session.Id == limited.SessionId)).LicenseId);
        Assert.Equal(1, (await db.UsageMonthly.AsNoTracking().SingleAsync(usage => usage.LicenseId == seed.LicenseId)).SessionsIssued);
        Assert.Equal(1, (await db.UsageMonthly.AsNoTracking().SingleAsync(usage => usage.LicenseId == unlimited.Id)).SessionsIssued);
    }

    [PostgresFact]
    public async Task ChangingModePreservesUsageAndAppliesTheCurrentLimitedQuota()
    {
        var seed = await SeedAsync(); PlaybackSessionResponse first;
        await using (var db = Db())
        {
            var service = Service(db, seed); first = await service.IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "mode-first", default);
            await service.IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "mode-second", default); await service.IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "mode-third", default);
        }
        await using var app = App(); using var admin = app.CreateClient(); admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminValidationTests.AdminKey);
        var path = $"/admin/v1/partners/{seed.Identity.PartnerId}/licenses/{seed.LicenseId}";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(path, new LicenseInput("Now limited", "active", seed.Now.AddDays(-1), seed.Now.AddDays(1), 2, false))).StatusCode);
        await using (var db = Db())
        {
            var service = Service(db, seed); var rejected = await Assert.ThrowsAsync<ApiException>(() => service.IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "mode-exhausted", default)); Assert.Equal("monthly_quota_exceeded", rejected.Code);
            var replay = await service.IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "mode-first", default); Assert.True(replay.Replayed); Assert.Equal(first.SessionId, replay.SessionId);
            Assert.Equal(3, (await db.UsageMonthly.AsNoTracking().SingleAsync(usage => usage.LicenseId == seed.LicenseId)).SessionsIssued);
        }
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(path, new LicenseInput("Unlimited again", "active", seed.Now.AddDays(-1), seed.Now.AddDays(1), 0, true))).StatusCode);
        await using var verify = Db(); await Service(verify, seed).IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "mode-fourth", default);
        Assert.Equal(4, (await verify.UsageMonthly.AsNoTracking().SingleAsync(usage => usage.LicenseId == seed.LicenseId)).SessionsIssued);
    }

    [PostgresFact]
    public async Task AnUnlimitedLicenseForAnotherCatalogueCannotReplaceTheLimitedQuota()
    {
        var seed = await SeedAsync(unlimited: false, quota: 0); var unrelated = await SeedAsync(); await using var db = Db();
        var unlimited = new License { PartnerId = seed.Identity.PartnerId, Name = "Other catalogue", StartsAt = seed.Now.AddDays(-1), EndsAt = seed.Now.AddDays(1), UnlimitedSessions = true, MonthlySessionQuota = 0 };
        db.AddRange(unlimited, new LicenseVideo { LicenseId = unlimited.Id, VideoId = unrelated.VideoId }); await db.SaveChangesAsync();
        var rejected = await Assert.ThrowsAsync<ApiException>(() => Service(db, seed).IssueAsync(seed.Identity, seed.VideoId, seed.AssetId, "wrong-unlimited-catalogue", default));
        Assert.Equal("monthly_quota_exceeded", rejected.Code); Assert.False(await db.UsageMonthly.AnyAsync(usage => usage.PartnerId == seed.Identity.PartnerId));
    }

    [PostgresFact]
    public async Task CreateAndLegacyUpdatePreserveExplicitModeAndNormalizeQuota()
    {
        var seed = await SeedAsync(); await using var app = App(); using var admin = app.CreateClient(); admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminValidationTests.AdminKey);
        var path = $"/admin/v1/partners/{seed.Identity.PartnerId}/licenses";
        object Legacy(long quota) => new { name = "Legacy test", status = "active", startsAt = seed.Now.AddDays(-1), endsAt = seed.Now.AddDays(1), monthlySessionQuota = quota };
        var created = await admin.PostAsJsonAsync(path, Legacy(5)); Assert.Equal(HttpStatusCode.Created, created.StatusCode); var limited = (await created.Content.ReadFromJsonAsync<LicenseView>())!; Assert.False(limited.UnlimitedSessions); Assert.Equal(5, limited.MonthlySessionQuota);
        var unlimitedCreate = await admin.PostAsJsonAsync(path, new LicenseInput("Unlimited test", "active", seed.Now.AddDays(-1), seed.Now.AddDays(1), 123, true));
        var unlimited = (await unlimitedCreate.Content.ReadFromJsonAsync<LicenseView>())!; Assert.True(unlimited.UnlimitedSessions); Assert.Equal(0, unlimited.MonthlySessionQuota);
        var omitted = await admin.PutAsJsonAsync(path + "/" + unlimited.Id, Legacy(999)); var preserved = (await omitted.Content.ReadFromJsonAsync<LicenseView>())!;
        Assert.True(preserved.UnlimitedSessions); Assert.Equal(0, preserved.MonthlySessionQuota);
        var nullFlag = await admin.PutAsJsonAsync(path + "/" + unlimited.Id, new LicenseInput("Null preserves", "active", seed.Now.AddDays(-1), seed.Now.AddDays(1), 999, null)); Assert.True((await nullFlag.Content.ReadFromJsonAsync<LicenseView>())!.UnlimitedSessions);
        var convert = await admin.PutAsJsonAsync(path + "/" + unlimited.Id, new LicenseInput("Now limited", "active", seed.Now.AddDays(-1), seed.Now.AddDays(1), 7, false)); var converted = (await convert.Content.ReadFromJsonAsync<LicenseView>())!; Assert.False(converted.UnlimitedSessions); Assert.Equal(7, converted.MonthlySessionQuota);
        var limitedUpdate = await admin.PutAsJsonAsync(path + "/" + limited.Id, Legacy(3)); Assert.False((await limitedUpdate.Content.ReadFromJsonAsync<LicenseView>())!.UnlimitedSessions);
        var list = (await admin.GetFromJsonAsync<AdminPage<LicenseView>>(path))!; Assert.Contains(list.Items, license => license.Id == seed.LicenseId && license.UnlimitedSessions);
    }

    [PostgresFact]
    public async Task UsageAndCsvExposeModeOnlyToAuthorizedScopesAndAppendColumn()
    {
        var seed = await SeedAsync(); var other = await SeedAsync();
        await using var app = App(); using var partner = app.CreateClient(); partner.DefaultRequestHeaders.Authorization = new("Bearer", seed.ApiKey);
        partner.DefaultRequestHeaders.Add("Idempotency-Key", "api-unlimited");
        Assert.Equal(HttpStatusCode.OK, (await partner.PostAsJsonAsync($"/v1/videos/{seed.VideoId}/playback-sessions", new PlaybackRequest(seed.AssetId))).StatusCode);
        var own = (await partner.GetFromJsonAsync<UsageResponse>("/v1/usage"))!; var item = Assert.Single(own.Licenses); Assert.True(item.UnlimitedSessions); Assert.Equal(seed.LicenseId, item.LicenseId); Assert.Equal(1, item.SessionsIssued);
        Assert.DoesNotContain(own.Licenses, license => license.LicenseId == other.LicenseId);
        var csvPath = $"/admin/v1/partners/{seed.Identity.PartnerId}/usage.csv";
        Assert.Equal(HttpStatusCode.Unauthorized, (await partner.GetAsync(csvPath)).StatusCode);
        using var anonymous = app.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(csvPath)).StatusCode);
        using var admin = app.CreateClient(); admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminValidationTests.AdminKey);
        var administrative = (await admin.GetFromJsonAsync<UsageResponse>($"/admin/v1/partners/{seed.Identity.PartnerId}/usage"))!; Assert.True(Assert.Single(administrative.Licenses).UnlimitedSessions);
        var rows = (await admin.GetStringAsync(csvPath)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(row => row.Split(',').Select(cell => cell.Trim('"')).ToArray()).ToArray();
        Assert.Equal(new[] { "record_type", "month", "partner_id", "license_id", "monthly_session_quota", "sessions_issued", "started_sessions", "completed_sessions", "unlimited_sessions" }, rows[0]);
        Assert.All(rows, row => Assert.Equal(9, row.Length)); Assert.Equal("true", rows[1][8]); Assert.Equal("", rows[2][8]); Assert.Equal("1", rows[1][5]);
        var deniedUpdate = await admin.PutAsJsonAsync($"/admin/v1/partners/{other.Identity.PartnerId}/licenses/{seed.LicenseId}", new LicenseInput("Other tenant", "active", seed.Now.AddDays(-1), seed.Now.AddDays(1), 1, false)); Assert.Equal(HttpStatusCode.NotFound, deniedUpdate.StatusCode);
        await using var check = Db(); Assert.True((await check.Licenses.SingleAsync(license => license.Id == seed.LicenseId)).UnlimitedSessions);
    }

    [PostgresFact]
    public async Task DatabaseDefaultForAnInsertWithoutTheNewColumnIsLimited()
    {
        var seed = await SeedAsync(); await using var db = Db(); var id = Guid.NewGuid(); var partner = seed.Identity.PartnerId; var now = seed.Now;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO videos.licenses (id, partner_id, name, status, starts_at, ends_at, monthly_session_quota, created_at)
            VALUES ({id}, {partner}, 'Legacy SQL test', 'active', {now}, {now.AddDays(1)}, 0, {now})
            """);
        Assert.False((await db.Licenses.SingleAsync(license => license.Id == id)).UnlimitedSessions);
    }
}

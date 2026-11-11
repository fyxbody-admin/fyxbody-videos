using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FyxBody.Videos.Api;
using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Api.Portal;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FyxBody.Videos.Tests;

public sealed class AdminPreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class RecordingSigner : IVideoUrlSigner
    {
        public List<(Guid AssetId, DateTimeOffset ExpiresAt)> Calls { get; } = [];
        public ApiException? Failure { get; init; }
        public Task<Uri> CreateReadUrlAsync(VideoAsset asset, DateTimeOffset expiresAt, CancellationToken ct)
        {
            Calls.Add((asset.Id, expiresAt));
            if (Failure is not null) throw Failure;
            return Task.FromResult(new Uri("https://previewaccount.blob.core.windows.net/videos/test.mp4?sig=test-only"));
        }
    }
    private static WebApplicationFactory<Program> Factory(RecordingSigner signer, string? account = "previewaccount", bool database = true) =>
        AdminValidationTests.CreateApplication(database ? LocalConnection() : "Host=127.0.0.1;Port=1;Database=fyxbody_videos;Username=test;Timeout=1")
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:AccountName"] = account }));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IVideoUrlSigner>(); services.AddSingleton<IVideoUrlSigner>(signer);
                    services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(new FixedClock());
                });
            });
    private static string LocalConnection()
    {
        var connection = Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")!;
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not ("127.0.0.1" or "localhost") || settings.Database != "fyxbody_videos")
            throw new InvalidOperationException("A disposable local database is required.");
        return connection;
    }
    private static string Preview(VideoAsset asset, Guid? videoId = null) => $"/admin/v1/videos/{videoId ?? asset.VideoId}/assets/{asset.Id}/preview";
    private static HttpClient Admin(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient(); client.DefaultRequestHeaders.Add("X-Admin-Key", AdminValidationTests.AdminKey); return client;
    }
    private static async Task<VideoAsset> SeedAsset(WebApplicationFactory<Program> app, string videoStatus = "draft", string assetStatus = "ready")
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
        var video = new Video { ExternalExerciseId = Guid.NewGuid().ToString("N"), Title = "Admin preview test", Status = videoStatus };
        var asset = new VideoAsset { VideoId = video.Id, BlobContainer = "videos", BlobName = Guid.NewGuid() + ".mp4", SizeBytes = 123, DurationMs = 1000, Status = assetStatus };
        db.AddRange(video, asset); await db.SaveChangesAsync(); return asset;
    }

    [Theory]
    [InlineData("previewaccount", "'self' blob: https://previewaccount.blob.core.windows.net")]
    [InlineData(null, "'self' blob:")]
    [InlineData("bad.example; media-src *", "'self' blob:")]
    [InlineData("previewaccount\n", "'self' blob:")]
    public async Task CspAllowsOnlyTheConfiguredValidAzureAccount(string? account, string expected)
    {
        await using var app = Factory(new RecordingSigner(), account, database: false);
        using var client = app.CreateClient();
        var response = await client.GetAsync("/");
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal("media-src " + expected, Assert.Single(csp.Split(';', StringSplitOptions.TrimEntries), directive => directive.StartsWith("media-src ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PreviewIsAdminOnlyAndOpenApiDocumentsTypedResponseAndCsrf()
    {
        var signer = new RecordingSigner();
        await using var app = Factory(signer, database: false);
        using var client = app.CreateClient();
        var path = $"/admin/v1/videos/{Guid.NewGuid()}/assets/{Guid.NewGuid()}/preview";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(path, null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(path, null)).StatusCode);
        Assert.Empty(signer.Calls);
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/admin.json"));
        var operation = doc.RootElement.GetProperty("paths").GetProperty("/admin/v1/videos/{videoId}/assets/{assetId}/preview").GetProperty("post");
        Assert.Contains("ten-minute", operation.GetProperty("description").GetString());
        Assert.True(operation.GetProperty("responses").GetProperty("200").TryGetProperty("content", out _));
        Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "X-CSRF-TOKEN");
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("AdminKey", out _));
    }

    [PostgresFact]
    public async Task ReadyDraftAndArchivedAssetsGetTenMinuteUrlWithoutSessionsEventsOrQuotaWrites()
    {
        var signer = new RecordingSigner();
        await using var app = Factory(signer);
        var draft = await SeedAsset(app); var archived = await SeedAsset(app, "archived");
        Guid partnerId; Guid licenseId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            var partner = new FyxBody.Videos.Data.Partner { Code = Guid.NewGuid().ToString("N"), Name = "Preview quota sentinel" };
            var license = new License { PartnerId = partner.Id, Name = "Unrelated quota", StartsAt = Now.AddDays(-1), EndsAt = Now.AddDays(1), MonthlySessionQuota = 10 };
            partnerId = partner.Id; licenseId = license.Id;
            db.AddRange(partner, license, new UsageMonthly { PartnerId = partnerId, LicenseId = licenseId, Month = new(2026, 11, 1), SessionsIssued = 7 });
            await db.SaveChangesAsync();
        }
        using var client = Admin(app);
        foreach (var asset in new[] { draft, archived })
        {
            var response = await client.PostAsync(Preview(asset), null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var result = (await response.Content.ReadFromJsonAsync<AssetPreviewView>())!;
            Assert.Equal(Now.AddMinutes(10), result.ExpiresAt);
            Assert.Equal("https://previewaccount.blob.core.windows.net/videos/test.mp4?sig=test-only", result.Url.ToString());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "expiresAt", "url" }, json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        }
        Assert.Equal(new[] { draft.Id, archived.Id }, signer.Calls.Select(call => call.AssetId));
        Assert.All(signer.Calls, call => Assert.Equal(Now.AddMinutes(10), call.ExpiresAt));
        await using var check = app.Services.CreateAsyncScope();
        var stored = check.ServiceProvider.GetRequiredService<VideosDbContext>();
        var assetIds = new[] { draft.Id, archived.Id };
        Assert.False(await stored.PlaybackSessions.AnyAsync(session => assetIds.Contains(session.VideoAssetId)));
        Assert.False(await stored.PlaybackEvents.AnyAsync(evt => stored.PlaybackSessions.Any(session => session.Id == evt.PlaybackSessionId && assetIds.Contains(session.VideoAssetId))));
        Assert.Equal(7, (await stored.UsageMonthly.SingleAsync(usage => usage.PartnerId == partnerId && usage.LicenseId == licenseId)).SessionsIssued);
    }

    [PostgresFact]
    public async Task MissingMismatchedPendingAndDisabledAssetsCannotBeSigned()
    {
        var signer = new RecordingSigner();
        await using var app = Factory(signer);
        var ready = await SeedAsset(app); var pending = await SeedAsset(app, assetStatus: "pending"); var disabled = await SeedAsset(app, assetStatus: "disabled");
        using var client = Admin(app);
        foreach (var path in new[] { Preview(ready, Guid.NewGuid()), $"/admin/v1/videos/{ready.VideoId}/assets/{Guid.NewGuid()}/preview" })
        {
            var missing = await client.PostAsync(path, null);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode); Assert.Contains("not_found", await missing.Content.ReadAsStringAsync());
        }
        foreach (var asset in new[] { pending, disabled })
        {
            var unavailable = await client.PostAsync(Preview(asset), null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode); Assert.Contains("video_unavailable", await unavailable.Content.ReadAsStringAsync());
        }
        Assert.Empty(signer.Calls);
    }

    [PostgresFact]
    public async Task StorageFailureDoesNotReturnAUrlOrCreateSession()
    {
        var signer = new RecordingSigner { Failure = new ApiException(503, "video_storage_unavailable", "Video storage is temporarily unavailable.") };
        await using var app = Factory(signer); var asset = await SeedAsset(app);
        using var client = Admin(app); var response = await client.PostAsync(Preview(asset), null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadAsStringAsync(); Assert.Contains("video_storage_unavailable", body); Assert.DoesNotContain("sig=", body);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<VideosDbContext>().PlaybackSessions.AnyAsync(session => session.VideoAssetId == asset.Id));
    }

    [PostgresFact]
    public async Task PortalPartnerIsForbiddenAndAdministratorMustSendCsrf()
    {
        var signer = new RecordingSigner(); await using var app = Factory(signer); var asset = await SeedAsset(app);
        foreach (var role in new[] { "partner", "admin" })
        {
            var username = Guid.NewGuid().ToString("N"); const string password = "Preview-only-test-password-73";
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
                var user = new PortalUser { Username = username, NormalizedUsername = username.ToUpperInvariant(), DisplayName = "Preview test", Role = role, MustChangePassword = false, CreatedAt = Now, UpdatedAt = Now };
                if (role == "partner") { var partner = new FyxBody.Videos.Data.Partner { Code = username, Name = "Preview test partner" }; db.Add(partner); user.PartnerId = partner.Id; }
                user.PasswordHash = scope.ServiceProvider.GetRequiredService<PortalPasswords>().Hash(user, password); db.Add(user); await db.SaveChangesAsync();
            }
            using var client = app.CreateClient();
            var session = (await client.GetFromJsonAsync<PortalSessionResponse>("/portal/api/session"))!;
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.CsrfToken);
            var login = await client.PostAsJsonAsync("/portal/api/login", new { username, password }); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            session = (await login.Content.ReadFromJsonAsync<PortalSessionResponse>())!;
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            if (role == "partner")
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(Preview(asset), null)).StatusCode);
            else
            {
                var denied = await client.PostAsync(Preview(asset), null); Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); Assert.Contains("invalid_csrf", await denied.Content.ReadAsStringAsync());
                client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.CsrfToken);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Preview(asset), null)).StatusCode);
            }
        }
        Assert.Single(signer.Calls);
    }
}

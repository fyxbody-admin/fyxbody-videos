using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FyxBody.Videos.Api;
using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Api.Portal;
using FyxBody.Videos.Api.YouTube;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace FyxBody.Videos.Tests;

public sealed partial class YouTubeImportTests
{
    private static string Id() => Guid.NewGuid().ToString("N")[..11];
    private sealed class FakeMedia : IYouTubeMediaProcessor
    {
        public List<int[]> Calls { get; } = [];
        public int[]? ActualHeights { get; set; }
        public int SourceHeight { get; set; } = 1080;
        public Func<CancellationToken, Task>? BeforePrepare { get; set; }
        public async Task<PreparedYouTubeVideo> PrepareAsync(string youtubeId, IReadOnlyList<int> heights, Func<MediaImportProgress, Task> progress, CancellationToken ct)
        {
            Calls.Add(heights.ToArray());
            if (BeforePrepare is not null) await BeforePrepare(ct);
            await progress(new("probing", 5)); await progress(new("downloading", 12)); await progress(new("transcoding", 78));
            var path = Path.Combine(Path.GetTempPath(), "fyxbody-job-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
            var outputs = (ActualHeights ?? heights.ToArray()).Select(height => {
                var file = Path.Combine(path, height + ".mp4"); File.WriteAllBytes(file, new byte[32]); return new PreparedYouTubeRendition(height, file, 32, 1000);
            }).ToArray();
            return new("Media title", SourceHeight, 1000, outputs, () => { Directory.Delete(path, true); return ValueTask.CompletedTask; });
        }
    }
    private sealed class FakeStorage : IVideoAssetStorage
    {
        public List<VideoAsset> Uploads { get; } = [];
        public List<Guid> Deletes { get; } = [];
        public int? FailHeight { get; set; }
        public Func<VideoAsset, CancellationToken, Task>? OnUpload { get; set; }
        public async Task UploadAsync(VideoAsset asset, Stream content, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Uploads.Add(asset); Assert.Equal(asset.SizeBytes, content.Length);
            if (OnUpload is not null) await OnUpload(asset, ct);
            if (asset.Rendition == FailHeight + "p") throw new ApiException(503, "video_storage_unavailable", "Private diagnostics must not be exposed.");
        }
        public Task DeleteAsync(VideoAsset asset, CancellationToken ct) { Deletes.Add(asset.Id); return Task.CompletedTask; }
    }
    private sealed class TestApp : IAsyncDisposable
    {
        public FakeMedia Media { get; } = new(); public FakeStorage Storage { get; } = new();
        public WebApplicationFactory<Program> App { get; }
        public ConcurrentBag<Guid> Jobs { get; } = [];
        public TestApp(bool database = true, bool enabled = true, int downloads = 1)
        {
            var connection = database ? LocalConnection() : "Host=127.0.0.1;Port=1;Database=fyxbody_videos;Username=test;Timeout=1";
            App = AdminValidationTests.CreateApplication(connection).WithWebHostBuilder(builder => {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["VideoImports:Enabled"] = enabled.ToString(), ["VideoImports:MaxConcurrentDownloads"] = downloads.ToString(), ["Storage:UploadContainer"] = "video", ["Storage:AllowedContainers:0"] = "video" }));
                builder.ConfigureServices(services => {
                    foreach (var descriptor in services.Where(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(YouTubeImportWorker)).ToArray()) services.Remove(descriptor);
                    services.RemoveAll<IYouTubeMediaProcessor>(); services.AddSingleton<IYouTubeMediaProcessor>(Media);
                    services.RemoveAll<IVideoAssetStorage>(); services.AddSingleton<IVideoAssetStorage>(Storage);
                });
            });
        }
        private static string LocalConnection()
        {
            var connection = Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")!; var settings = new NpgsqlConnectionStringBuilder(connection);
            if (settings.Host is not ("localhost" or "127.0.0.1") || settings.Database != "fyxbody_videos") throw new InvalidOperationException("A disposable local database is required."); return connection;
        }
        public HttpClient Admin() { var client = App.CreateClient(); client.DefaultRequestHeaders.Add("X-Admin-Key", AdminValidationTests.AdminKey); return client; }
        public async Task<VideoImportView> Enqueue(string id, int[]? heights = null)
        {
            await using var scope = App.Services.CreateAsyncScope(); var result = await scope.ServiceProvider.GetRequiredService<YouTubeImportQueue>().Enqueue(new("https://youtu.be/" + id, heights), default);
            Jobs.Add(result.Id); return result;
        }
        public async Task<VideoImportJob?> Claim()
        { await using var scope = App.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<YouTubeImportQueue>().Claim(default); }
        public async Task Run(VideoImportJob job, CancellationToken ct = default)
        { await using var scope = App.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<YouTubeImportRunner>().Run(job, ct); }
        public async Task<VideoImportView> Get(Guid id)
        { await using var scope = App.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<YouTubeImportQueue>().Get(id, default); }
        public async ValueTask DisposeAsync()
        {
            if (!Jobs.IsEmpty) { await using var scope = App.Services.CreateAsyncScope(); var ids = Jobs.ToArray(); await scope.ServiceProvider.GetRequiredService<VideosDbContext>().VideoImportJobs.Where(job => ids.Contains(job.Id)).ExecuteDeleteAsync(); }
            await App.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task RateLimitSurvivesHostRecreationAndAdmitsOneProbeBeforeParallelWork()
    {
        await using var app = new TestApp(downloads: 3);
        var first = await app.Enqueue(Id());
        var second = await app.Enqueue(Id());
        var third = await app.Enqueue(Id());
        var firstDownload = Assert.IsType<VideoImportJob>(await app.Claim());
        var secondDownload = Assert.IsType<VideoImportJob>(await app.Claim());
        app.Media.BeforePrepare = _ => throw new ApiException(429, "youtube_rate_limited", "Rate limited");
        await app.Run(firstDownload);
        await app.Run(secondDownload);

        await using var restarted = new TestApp(downloads: 3);
        Assert.Null(await restarted.Claim());
        Assert.Equal("queued", (await restarted.Get(first.Id)).Status);
        Assert.Equal("youtube_rate_limited", (await restarted.Get(second.Id)).ErrorCode);

        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.VideoImportJobs.Where(job => job.Id == first.Id || job.Id == second.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.CreatedAt, now.AddMinutes(-1))
                .SetProperty(job => job.UpdatedAt, now));
        }

        var recovery = Assert.Single((await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => restarted.Claim())))
            .OfType<VideoImportJob>());
        Assert.Contains(recovery.Id, new[] { first.Id, second.Id });
        Assert.Equal("youtube_rate_limited", recovery.ErrorCode);
        await restarted.Run(recovery);
        var remaining = (await Task.WhenAll(restarted.Claim(), restarted.Claim())).OfType<VideoImportJob>().ToArray();
        Assert.Equal(2, remaining.Length);
        Assert.Contains(remaining, job => job.Id == third.Id);
        Assert.All(remaining, job => Assert.Null(job.ErrorCode));
    }

    [PostgresFact]
    public async Task TakeoutClaimsOnlyMappedJobsWhileNetworkQueueIsPaused()
    {
        var directory = Directory.CreateTempSubdirectory("fyxbody-takeout-claim-");
        try
        {
            await using var app = new TestApp(downloads: 3);
            var paused = await app.Enqueue(Id());
            var unmapped = await app.Enqueue(Id());
            var mappedId = Id();
            var mapped = await app.Enqueue(mappedId);
            app.Media.BeforePrepare = _ => throw new ApiException(429, "youtube_rate_limited", "Rate limited");
            var networkJob = Assert.IsType<VideoImportJob>(await app.Claim());
            Assert.Equal(paused.Id, networkJob.Id);
            await app.Run(networkJob);

            File.WriteAllText(Path.Combine(directory.FullName, "youtube-id-map.json"),
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [mappedId] = "source.mp4" }));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["VideoImports:TakeoutOnly"] = "true", ["VideoImports:TakeoutDirectory"] = directory.FullName
            }).Build();
            await using var scope = app.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            var takeout = new YouTubeImportQueue(db, config, TimeProvider.System, new(config));
            Assert.Equal(mapped.Id, Assert.IsType<VideoImportJob>(await takeout.Claim(default)).Id);
            Assert.Null(await takeout.Claim(default));
            Assert.Null(await app.Claim());
            Assert.Equal("youtube_rate_limited", (await app.Get(paused.Id)).ErrorCode);
            Assert.Equal("queued", (await app.Get(unmapped.Id)).Status);
        }
        finally { directory.Delete(recursive: true); }
    }

    [PostgresFact]
    public async Task ConcurrentClaimsRespectDownloadLimitAndReleaseSlotDuringTranscode()
    {
        await using var app = new TestApp(downloads: 3);
        for (var index = 0; index < 5; index++) await app.Enqueue(Id());
        var active = (await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => app.Claim()))).OfType<VideoImportJob>().ToArray();
        Assert.Equal(3, active.Length);
        Assert.Equal(3, active.Select(job => job.Id).Distinct().Count());

        await using var scope = app.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
        await db.VideoImportJobs.Where(job => job.Id == active[0].Id).ExecuteUpdateAsync(setters =>
            setters.SetProperty(job => job.Stage, "transcoding"));
        Assert.IsType<VideoImportJob>(await app.Claim());
        Assert.Null(await app.Claim());
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=WhpknPP0xKU")]
    [InlineData("https://youtube.com/watch?v=WhpknPP0xKU")]
    [InlineData("https://m.youtube.com/watch?v=WhpknPP0xKU")]
    [InlineData("https://youtu.be/WhpknPP0xKU")]
    [InlineData("https://www.youtube.com/shorts/WhpknPP0xKU")]
    [InlineData("https://www.youtube.com/embed/WhpknPP0xKU")]
    [InlineData("https://youtu.be/WhpknPP0xKU?si=track&t=10")]
    [InlineData("https://www.youtube.com/watch?v=WhpknPP0xKU&feature=share&start=10")]
    public void AcceptedUrlsCanonicalizeToTheSingleVideoId(string url)
    {
        var value = YouTubeImportValidation.Input(new(url)); Assert.Equal("WhpknPP0xKU", value.YoutubeId);
        Assert.Equal("https://www.youtube.com/watch?v=WhpknPP0xKU", value.SourceUrl); Assert.Equal(new[] { 1080, 720, 480 }, value.Heights);
    }
    [Theory]
    [InlineData("http://www.youtube.com/watch?v=WhpknPP0xKU")]
    [InlineData("https://user@youtube.com/watch?v=WhpknPP0xKU")]
    [InlineData("https://youtube.com:443/watch?v=WhpknPP0xKU")]
    [InlineData("https://youtube.com.evil.test/watch?v=WhpknPP0xKU")]
    [InlineData("https://127.0.0.1/watch?v=WhpknPP0xKU")]
    [InlineData("https://youtube.com/watch?v=WhpknPP0xKU&list=other")]
    [InlineData("https://youtube.com/watch?v=WhpknPP0xKU#fragment")]
    [InlineData("https://youtube.com/playlist?list=WhpknPP0xKU")]
    [InlineData("https://youtu.be//WhpknPP0xKU")]
    [InlineData("https://youtube.com/shorts/WhpknPP0xKU/extra")]
    [InlineData("https://youtube.com/embed/%57hpknPP0xKU")]
    [InlineData("file:///etc/passwd")]
    [InlineData("--exec=https://youtu.be/WhpknPP0xKU")]
    public void ArbitraryHostsArgumentsPlaylistsAndNavigationAreRejected(string url) => Assert.Throws<ApiException>(() => YouTubeImportValidation.Input(new(url)));

    [Fact]
    public async Task DisabledCapabilitiesAndEnqueueNeedNoDatabaseAndAnonymousCannotReadThem()
    {
        await using var app = new TestApp(database: false, enabled: false); using var anonymous = app.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/admin/v1/video-imports/capabilities")).StatusCode);
        using var admin = app.Admin(); var caps = (await admin.GetFromJsonAsync<VideoImportCapabilities>("/admin/v1/video-imports/capabilities"))!;
        Assert.False(caps.Enabled); Assert.Equal(600, caps.MaxDurationSeconds); Assert.Equal(200L * 1024 * 1024, caps.MaxFileBytes);
        var disabled = await admin.PostAsJsonAsync("/admin/v1/video-imports", new VideoImportInput("https://youtu.be/WhpknPP0xKU"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode); Assert.Contains("video_imports_disabled", await disabled.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task ConcurrentEnqueueDeduplicatesAndAtomicClaimsHaveOnlyOneWinner()
    {
        await using var app = new TestApp(); var id = Id(); var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => app.Enqueue(id)));
        Assert.Single(results.Select(job => job.Id).Distinct()); var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => app.Claim()));
        var claimed = Assert.Single(claims, job => job is not null)!; Assert.Equal(results[0].Id, claimed.Id); Assert.Equal("running", claimed.Status);
        Assert.NotNull(claimed.LeaseId); Assert.True(claimed.LeaseExpiresAt > claimed.UpdatedAt.AddMinutes(29));
    }

    [PostgresFact]
    public async Task ActiveJobMayBeReusedForSubsetButAdditionalHeightsRequireWaiting()
    {
        await using var app = new TestApp(); var id = Id(); var first = await app.Enqueue(id, [1080, 720]);
        var subset = await app.Enqueue(id, [720]); Assert.Equal(first.Id, subset.Id);
        var conflict = await Assert.ThrowsAsync<ApiException>(() => app.Enqueue(id, [480]));
        Assert.Equal(409, conflict.Status); Assert.Equal("import_in_progress", conflict.Code);
        Assert.Equal(new[] { 1080, 720 }, (await app.Get(first.Id)).RequestedHeights);
    }

    private static async Task<Video> Existing(TestApp app, string id, params int[] heights)
    {
        await using var scope = app.App.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
        var video = new Video { ExternalExerciseId = "youtube:" + id, Title = "Existing custom title", Status = "active" }; db.Videos.Add(video);
        db.VideoAssets.AddRange(heights.Select(height => new VideoAsset { VideoId = video.Id, BlobContainer = "video", BlobName = Guid.NewGuid() + ".mp4", Language = "es", Rendition = height + "p", SizeBytes = 32, DurationMs = 1000, Status = "ready" }));
        await db.SaveChangesAsync(); return video;
    }

    [PostgresFact]
    public async Task ReadyExistingRenditionsCompleteWithoutDownloadingOrDuplicateExercise()
    {
        await using var app = new TestApp(); var id = Id(); var video = await Existing(app, id, 1080, 720, 480); var job = await app.Enqueue(id);
        await app.Run((await app.Claim())!); var done = await app.Get(job.Id);
        Assert.Equal("completed", done.Status); Assert.Equal(100, done.ProgressPercent); Assert.Equal(video.Id, done.VideoId); Assert.Equal(new[] { 1080, 720, 480 }, done.CompletedHeights);
        Assert.Empty(app.Media.Calls); Assert.Empty(app.Storage.Uploads);
    }

    [PostgresFact]
    public async Task PartialFailurePreservesReadyAssetsAndExplicitRetryProcessesOnlyMissingHeights()
    {
        await using var app = new TestApp(); var id = Id(); app.Storage.FailHeight = 720; var first = await app.Enqueue(id);
        await app.Run((await app.Claim())!); var failed = await app.Get(first.Id);
        Assert.Equal("failed", failed.Status); Assert.Equal(new[] { 1080 }, failed.CompletedHeights); Assert.Equal("video_storage_unavailable", failed.ErrorCode);
        Assert.DoesNotContain("Private", failed.ErrorMessage!); Assert.Single(app.Storage.Deletes);
        Guid original;
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>(); var asset = await db.VideoAssets.SingleAsync(value => value.VideoId == failed.VideoId);
            original = asset.Id; Assert.Equal("1080p", asset.Rendition); Assert.Equal("active", (await db.Videos.SingleAsync(video => video.Id == failed.VideoId)).Status);
        }
        app.Storage.FailHeight = null; var retry = await app.Enqueue(id); Assert.NotEqual(first.Id, retry.Id); await app.Run((await app.Claim())!);
        var done = await app.Get(retry.Id); Assert.Equal("completed", done.Status); Assert.Equal(failed.VideoId, done.VideoId); Assert.Equal(new[] { 1080, 720, 480 }, done.CompletedHeights);
        Assert.Equal(new[] { 720, 480 }, app.Media.Calls[1]);
        await using var check = app.App.Services.CreateAsyncScope(); var stored = check.ServiceProvider.GetRequiredService<VideosDbContext>();
        Assert.True(await stored.VideoAssets.AnyAsync(asset => asset.Id == original)); Assert.Equal(3, await stored.VideoAssets.CountAsync(asset => asset.VideoId == done.VideoId));
        Assert.False(await stored.PlaybackSessions.AnyAsync(session => session.VideoId == done.VideoId));
    }

    [PostgresFact]
    public async Task SourceHeightFallbackIsPersistedWithoutUpscalingAndExistingTitlesArePreserved()
    {
        await using var app = new TestApp(); var id = Id(); var video = await Existing(app, id); app.Media.SourceHeight = 239; app.Media.ActualHeights = [238];
        var job = await app.Enqueue(id, [1080]); await app.Run((await app.Claim())!); var done = await app.Get(job.Id);
        Assert.Equal("completed", done.Status); Assert.Equal(new[] { 238 }, done.CompletedHeights); Assert.Equal(video.Id, done.VideoId);
        var asset = Assert.Single(app.Storage.Uploads); Assert.Equal("238p", asset.Rendition); Assert.Equal($"ejercicios/{video.Id:D}/es/238p/{asset.Id:D}.mp4", asset.BlobName);
        await using var scope = app.App.Services.CreateAsyncScope(); Assert.Equal("Existing custom title", (await scope.ServiceProvider.GetRequiredService<VideosDbContext>().Videos.SingleAsync(value => value.Id == video.Id)).Title);
    }

    [PostgresFact]
    public async Task ExpiredLeasesRequeueTheSameJobAndStaleOwnersCannotStartMedia()
    {
        await using var app = new TestApp(); var id = Id(); var job = await app.Enqueue(id); var claimed = (await app.Claim())!;
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>(); await db.VideoImportJobs.Where(value => value.Id == job.Id).ExecuteUpdateAsync(update => update.SetProperty(value => value.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<YouTubeImportQueue>().ExpireLeases(default));
        }
        var retry = Assert.IsType<VideoImportJob>(await app.Claim());
        Assert.Equal(job.Id, retry.Id); Assert.NotEqual(claimed.LeaseId, retry.LeaseId);
        await app.Run(claimed); Assert.Empty(app.Media.Calls); Assert.Empty(app.Storage.Uploads);
    }

    [PostgresFact]
    public async Task HostCancellationRequeuesRunningJobAndClearsLease()
    {
        await using var app = new TestApp(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Media.BeforePrepare = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var job = await app.Enqueue(Id()); var claimed = (await app.Claim())!; using var stopping = new CancellationTokenSource();
        var running = app.Run(claimed, stopping.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stopping.Cancel(); await running;
        var resumed = Assert.IsType<VideoImportJob>(await app.Claim());
        Assert.Equal(job.Id, resumed.Id); Assert.NotEqual(claimed.LeaseId, resumed.LeaseId); Assert.Empty(app.Storage.Uploads);
    }

    [PostgresFact]
    public async Task LeaseLostDuringUploadCannotPublishAndCompensatesItsOwnBlob()
    {
        await using var app = new TestApp(); var queued = await app.Enqueue(Id(), [360]); var claimed = (await app.Claim())!;
        app.Storage.OnUpload = async (_, _) => {
            await using var scope = app.App.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            await db.VideoImportJobs.Where(job => job.Id == queued.Id).ExecuteUpdateAsync(update => update.SetProperty(job => job.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await scope.ServiceProvider.GetRequiredService<YouTubeImportQueue>().ExpireLeases(default);
        };
        await app.Run(claimed); var requeued = await app.Get(queued.Id); Assert.Equal("queued", requeued.Status); Assert.Null(requeued.ErrorCode);
        Assert.Single(app.Storage.Uploads); Assert.Single(app.Storage.Deletes);
        await using var check = app.App.Services.CreateAsyncScope(); Assert.False(await check.ServiceProvider.GetRequiredService<VideosDbContext>().VideoAssets.AnyAsync(asset => asset.VideoId == requeued.VideoId));
    }

    [PostgresFact]
    public async Task HostCancellationDuringStorageUploadCancelsAndCompensates()
    {
        await using var app = new TestApp(); var queued = await app.Enqueue(Id(), [360]); var claimed = (await app.Claim())!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Storage.OnUpload = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using var stopping = new CancellationTokenSource(); var running = app.Run(claimed, stopping.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stopping.Cancel(); await running;
        var requeued = await app.Get(queued.Id); Assert.Equal("queued", requeued.Status); Assert.Null(requeued.ErrorCode);
        Assert.Single(app.Storage.Uploads); Assert.Single(app.Storage.Deletes);
    }

    [PostgresFact]
    public async Task PartnerIsForbiddenAndAdminCookieNeedsCsrfToQueue()
    {
        await using var app = new TestApp(); var id = Id();
        foreach (var role in new[] { "partner", "admin" })
        {
            var username = Guid.NewGuid().ToString("N"); const string password = "Test-import-password-79";
            await using (var scope = app.App.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>(); var user = new PortalUser { Username = username, NormalizedUsername = username.ToUpperInvariant(), DisplayName = "Import test", Role = role, MustChangePassword = false };
                if (role == "partner") { var partner = new FyxBody.Videos.Data.Partner { Code = username, Name = "Import test" }; db.Partners.Add(partner); user.PartnerId = partner.Id; }
                user.PasswordHash = scope.ServiceProvider.GetRequiredService<PortalPasswords>().Hash(user, password); db.PortalUsers.Add(user); await db.SaveChangesAsync();
            }
            using var client = app.App.CreateClient(); var session = (await client.GetFromJsonAsync<PortalSessionResponse>("/portal/api/session"))!; client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.CsrfToken);
            var login = await client.PostAsJsonAsync("/portal/api/login", new { username, password }); Assert.Equal(HttpStatusCode.OK, login.StatusCode); session = (await login.Content.ReadFromJsonAsync<PortalSessionResponse>())!;
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); var denied = await client.PostAsJsonAsync("/admin/v1/video-imports", new VideoImportInput("https://youtu.be/" + id));
            Assert.Equal(role == "admin" ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden, denied.StatusCode);
            foreach (var path in new[] { "/admin/v1/video-imports/csv", "/admin/v1/video-imports/csv/preview" })
                Assert.Equal(role == "admin" ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new { csv = CsvText(id) })).StatusCode);
            if (role == "admin")
            {
                client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.CsrfToken); var accepted = await client.PostAsJsonAsync("/admin/v1/video-imports", new VideoImportInput("https://youtu.be/" + id));
                var preview = await client.PostAsJsonAsync("/admin/v1/video-imports/csv/preview", new { csv = CsvText(id) }); Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
                var bulk = await client.PostAsJsonAsync("/admin/v1/video-imports/csv", new { csv = CsvText(id) }); Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
                Assert.True((await bulk.Content.ReadFromJsonAsync<VideoImportCsvReport>())!.Queued);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); var queued = (await accepted.Content.ReadFromJsonAsync<VideoImportView>())!; app.Jobs.Add(queued.Id);
                Assert.NotNull(await client.GetFromJsonAsync<VideoImportView>("/admin/v1/video-imports/" + queued.Id));
                var page = (await client.GetFromJsonAsync<AdminPage<VideoImportView>>("/admin/v1/video-imports"))!; Assert.Contains(page.Items, value => value.Id == queued.Id);
            }
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FyxBody.Videos.Api;
using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Api.Portal;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FyxBody.Videos.Tests;

public sealed class AdminUploadTests
{
    private sealed class FakeStorage : IVideoAssetStorage
    {
        public List<VideoAsset> Uploads { get; } = [];
        public List<VideoAsset> Deletes { get; } = [];
        public Func<VideoAsset, Task>? OnUpload { get; set; }
        public bool FailUpload { get; set; }
        public bool FailDelete { get; set; }
        public async Task UploadAsync(VideoAsset asset, Stream content, CancellationToken ct)
        {
            Assert.IsType<FileStream>(content); Assert.True(content.CanSeek); Assert.Equal(asset.SizeBytes, content.Length);
            Uploads.Add(asset);
            if (OnUpload is not null) await OnUpload(asset);
            if (FailUpload) throw new ApiException(503, "video_storage_unavailable", "Test storage unavailable.");
        }
        public Task DeleteAsync(VideoAsset asset, CancellationToken ct)
        { Deletes.Add(asset); return FailDelete ? Task.FromException(new IOException("Test cleanup failed.")) : Task.CompletedTask; }
    }
    private static WebApplicationFactory<Program> Factory(FakeStorage storage, bool database = true, string? container = "video") =>
        AdminValidationTests.CreateApplication(database ? LocalConnection() : "Host=127.0.0.1;Port=1;Database=fyxbody_videos;Username=test;Timeout=1")
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Storage:UploadContainer"] = container, ["Storage:AllowedContainers:0"] = "video", ["Storage:AccountName"] = "uploadtestaccount" }));
                builder.ConfigureServices(services => { services.RemoveAll<IVideoAssetStorage>(); services.AddSingleton<IVideoAssetStorage>(storage); });
            });
    private static string LocalConnection()
    {
        var connection = Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")!; var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not ("127.0.0.1" or "localhost") || settings.Database != "fyxbody_videos") throw new InvalidOperationException("A disposable local database is required.");
        return connection;
    }
    private static HttpClient Admin(WebApplicationFactory<Program> app)
    { var client = app.CreateClient(); client.DefaultRequestHeaders.Add("X-Admin-Key", AdminValidationTests.AdminKey); return client; }
    private static string Upload(Guid videoId) => $"/admin/v1/videos/{videoId}/assets/upload";
    private static byte[] Mp4(int size = 32)
    {
        var bytes = new byte[size]; bytes[3] = 24; Encoding.ASCII.GetBytes("ftypisom", bytes.AsSpan(4)); return bytes;
    }
    private static MultipartFormDataContent Form(byte[]? bytes = null, string filename = "exercise.mp4", string language = "es", string rendition = "original", string duration = "1234", string contentType = "video/mp4")
    {
        var form = new MultipartFormDataContent(); form.Add(new StringContent(language), "language"); form.Add(new StringContent(rendition), "rendition"); form.Add(new StringContent(duration), "durationMs");
        var file = new ByteArrayContent(bytes ?? Mp4()); file.Headers.ContentType = new(contentType); form.Add(file, "file", filename); return form;
    }
    private static async Task<Video> Seed(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
        var video = new Video { ExternalExerciseId = Guid.NewGuid().ToString("N"), Title = "Upload test" }; db.Videos.Add(video); await db.SaveChangesAsync(); return video;
    }

    private sealed class BodySizeFeature : IHttpMaxRequestBodySizeFeature
    { public bool IsReadOnly => false; public long? MaxRequestBodySize { get; set; } = 262144; }

    [Fact]
    public async Task MultipartReaderRaisesOnlyItsRequestLimitAndSpoolsFileAboveDefaultKestrelLimit()
    {
        using var form = Form(Mp4(400000), filename: "../../ignored.mp4");
        var context = new DefaultHttpContext(); var size = new BodySizeFeature(); context.Features.Set<IHttpMaxRequestBodySizeFeature>(size);
        context.Request.ContentType = form.Headers.ContentType!.ToString(); context.Request.Body = await form.ReadAsStreamAsync();
        string temp;
        await using (var upload = await MultipartVideoUpload.ReadAsync(context.Request, default))
        {
            Assert.Equal(MultipartVideoUpload.MaximumRequestBytes, size.MaxRequestBodySize); Assert.Equal(400000, upload.File.Length);
            Assert.Equal("es", upload.Language); Assert.Equal("original", upload.Rendition); Assert.Equal(1234, upload.DurationMs);
            temp = upload.File.Name; Assert.True(System.IO.File.Exists(temp));
        }
        Assert.False(System.IO.File.Exists(temp));
    }

    [Fact]
    public async Task DeclaredOversizeRequestIsRejectedBeforeReadingOrSpooling()
    {
        var context = new DefaultHttpContext(); context.Request.ContentLength = MultipartVideoUpload.MaximumRequestBytes + 1;
        var error = await Assert.ThrowsAsync<ApiException>(() => MultipartVideoUpload.ReadAsync(context.Request, default));
        Assert.Equal(413, error.Status); Assert.Equal("upload_too_large", error.Code);
    }

    private sealed class OversizeMultipartStream : Stream
    {
        private readonly byte[] prefix = Encoding.ASCII.GetBytes("--limit-test\r\nContent-Disposition: form-data; name=\"file\"; filename=\"large.mp4\"\r\nContent-Type: video/mp4\r\n\r\n");
        private long offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
        public override int Read(Span<byte> buffer)
        {
            var remaining = prefix.Length + MultipartVideoUpload.MaximumFileBytes + 1 - offset;
            var count = (int)Math.Min(buffer.Length, remaining);
            buffer[..count].Clear();
            for (var index = 0; index < count && offset + index < prefix.Length; index++) buffer[index] = prefix[(int)offset + index];
            offset += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    [Fact]
    public async Task UnknownLengthStreamCannotExceedRealTwoHundredMiBLimit()
    {
        var context = new DefaultHttpContext(); context.Request.ContentType = "multipart/form-data; boundary=limit-test";
        context.Request.Body = new OversizeMultipartStream();
        var error = await Assert.ThrowsAsync<ApiException>(() => MultipartVideoUpload.ReadAsync(context.Request, default));
        Assert.Equal(413, error.Status); Assert.Equal("upload_too_large", error.Code);
    }

    [Theory]
    [InlineData("bad.txt", "video/mp4", "1234", true)]
    [InlineData("file.mp4", "text/html", "1234", true)]
    [InlineData("file.mp4", "video/mp4", "0", true)]
    [InlineData("file.mp4", "video/mp4", "2147483648", true)]
    [InlineData("file.mp4", "video/mp4", "1234", false)]
    public async Task InvalidFileOrDurationIsRejected(string filename, string mime, string duration, bool signature)
    {
        using var form = Form(signature ? Mp4() : new byte[32], filename, duration: duration, contentType: mime);
        var context = new DefaultHttpContext(); context.Request.ContentType = form.Headers.ContentType!.ToString(); context.Request.Body = await form.ReadAsStreamAsync();
        var error = await Assert.ThrowsAsync<ApiException>(() => MultipartVideoUpload.ReadAsync(context.Request, default)); Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task AnonymousUploadIsDeniedAndConcurrentUploadsAreBounded()
    {
        var storage = new FakeStorage(); await using var app = Factory(storage, database: false); using var anonymous = app.CreateClient();
        using var form = Form(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(Upload(Guid.NewGuid()), form)).StatusCode);
        var slots = app.Services.GetRequiredService<VideoUploadSlots>(); await slots.Gate.WaitAsync(); await slots.Gate.WaitAsync();
        try
        {
            using var admin = Admin(app); using var busyForm = Form(); var busy = await admin.PostAsync(Upload(Guid.NewGuid()), busyForm);
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode); Assert.NotNull(busy.Headers.RetryAfter);
        }
        finally { slots.Gate.Release(2); }
        Assert.Empty(storage.Uploads);
        using var doc = JsonDocument.Parse(await anonymous.GetStringAsync("/openapi/admin.json"));
        var operation = doc.RootElement.GetProperty("paths").GetProperty("/admin/v1/videos/{videoId}/assets/upload").GetProperty("post");
        Assert.True(operation.GetProperty("requestBody").GetProperty("content").TryGetProperty("multipart/form-data", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("201", out _)); Assert.True(operation.GetProperty("responses").TryGetProperty("413", out _));
    }

    [PostgresFact]
    public async Task UploadCreatesReadyPrivateAssetWithGeneratedPathAndNoPlaybackWrites()
    {
        var storage = new FakeStorage(); await using var app = Factory(storage); var video = await Seed(app); using var client = Admin(app);
        using var form = Form(Mp4(400000), filename: "../../untrusted.mp4", rendition: "720p", language: "en-US", contentType: "application/octet-stream");
        var response = await client.PostAsync(Upload(video.Id), form); Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var asset = (await response.Content.ReadFromJsonAsync<AssetView>())!;
        Assert.Equal("ready", asset.Status); Assert.Equal("video", asset.BlobContainer); Assert.Equal("video/mp4", asset.ContentType); Assert.Equal(400000, asset.SizeBytes);
        Assert.Equal($"ejercicios/{video.Id:D}/en-US/720p/{asset.Id:D}.mp4", asset.BlobName); Assert.Equal(1234, asset.DurationMs);
        Assert.EndsWith($"/assets/{asset.Id}", response.Headers.Location!.ToString()); Assert.Single(storage.Uploads); Assert.Empty(storage.Deletes);
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
        Assert.True(await db.VideoAssets.AnyAsync(value => value.Id == asset.Id)); Assert.False(await db.PlaybackSessions.AnyAsync(session => session.VideoAssetId == asset.Id));
        Assert.False(await db.PlaybackEvents.AnyAsync(evt => db.PlaybackSessions.Any(session => session.Id == evt.PlaybackSessionId && session.VideoAssetId == asset.Id)));
    }

    [PostgresFact]
    public async Task InvalidMetadataMissingVideoAndDuplicateRenditionDoNotUpload()
    {
        var storage = new FakeStorage(); await using var app = Factory(storage); var video = await Seed(app); using var client = Admin(app);
        using (var missing = Form()) Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(Upload(Guid.NewGuid()), missing)).StatusCode);
        foreach (var values in new[] { ("../es", "original"), ("es", "../720p"), ("es", "folder/rendition") })
        { using var invalid = Form(language: values.Item1, rendition: values.Item2); Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Upload(video.Id), invalid)).StatusCode); }
        Assert.Empty(storage.Uploads);
        using (var valid = Form()) Assert.Equal(HttpStatusCode.Created, (await client.PostAsync(Upload(video.Id), valid)).StatusCode);
        using (var duplicate = Form()) Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(Upload(video.Id), duplicate)).StatusCode);
        Assert.Single(storage.Uploads);
    }

    [PostgresFact]
    public async Task StorageFailureCompensatesWithoutCreatingDatabaseAsset()
    {
        var storage = new FakeStorage { FailUpload = true }; await using var app = Factory(storage); var video = await Seed(app); using var client = Admin(app); using var form = Form();
        var response = await client.PostAsync(Upload(video.Id), form); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Contains("video_storage_unavailable", await response.Content.ReadAsStringAsync());
        Assert.Single(storage.Uploads); Assert.Single(storage.Deletes);
        await using var scope = app.Services.CreateAsyncScope(); Assert.False(await scope.ServiceProvider.GetRequiredService<VideosDbContext>().VideoAssets.AnyAsync(asset => asset.VideoId == video.Id));
    }

    [PostgresFact]
    public async Task DatabaseRaceCompensatesUploadedBlobAndPreservesWinningAssetEvenIfCleanupFails()
    {
        var storage = new FakeStorage { FailDelete = true }; await using var app = Factory(storage); var video = await Seed(app); Guid winner = Guid.Empty;
        storage.OnUpload = async asset =>
        {
            await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            var other = new VideoAsset { VideoId = asset.VideoId, BlobContainer = "video", BlobName = "winner/" + Guid.NewGuid() + ".mp4", Language = asset.Language, Rendition = asset.Rendition, SizeBytes = 32, DurationMs = 1234, Status = "ready" };
            winner = other.Id; db.VideoAssets.Add(other); await db.SaveChangesAsync();
        };
        using var client = Admin(app); using var form = Form(); var response = await client.PostAsync(Upload(video.Id), form);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Single(storage.Deletes); Assert.Equal(Assert.Single(storage.Uploads).Id, storage.Deletes[0].Id);
        await using var check = app.Services.CreateAsyncScope(); var assets = await check.ServiceProvider.GetRequiredService<VideosDbContext>().VideoAssets.Where(asset => asset.VideoId == video.Id).ToArrayAsync();
        Assert.Equal(winner, Assert.Single(assets).Id);
    }

    [PostgresFact]
    public async Task CookieAdministratorRequiresCsrfAndPartnerCannotUpload()
    {
        var storage = new FakeStorage(); await using var app = Factory(storage); var video = await Seed(app);
        foreach (var role in new[] { "partner", "admin" })
        {
            var username = Guid.NewGuid().ToString("N"); const string password = "Test-video-upload-password-73";
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>(); var user = new PortalUser { Username = username, NormalizedUsername = username.ToUpperInvariant(), DisplayName = "Upload test", Role = role, MustChangePassword = false };
                if (role == "partner") { var partner = new FyxBody.Videos.Data.Partner { Code = username, Name = "Upload test" }; db.Partners.Add(partner); user.PartnerId = partner.Id; }
                user.PasswordHash = scope.ServiceProvider.GetRequiredService<PortalPasswords>().Hash(user, password); db.PortalUsers.Add(user); await db.SaveChangesAsync();
            }
            using var client = app.CreateClient(); var session = (await client.GetFromJsonAsync<PortalSessionResponse>("/portal/api/session"))!; client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.CsrfToken);
            var login = await client.PostAsJsonAsync("/portal/api/login", new { username, password }); Assert.Equal(HttpStatusCode.OK, login.StatusCode); session = (await login.Content.ReadFromJsonAsync<PortalSessionResponse>())!;
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); using var deniedForm = Form();
            var denied = await client.PostAsync(Upload(video.Id), deniedForm); Assert.Equal(role == "admin" ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden, denied.StatusCode);
            if (role == "admin") { client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.CsrfToken); using var valid = Form(); Assert.Equal(HttpStatusCode.Created, (await client.PostAsync(Upload(video.Id), valid)).StatusCode); }
        }
        Assert.Single(storage.Uploads);
    }
}

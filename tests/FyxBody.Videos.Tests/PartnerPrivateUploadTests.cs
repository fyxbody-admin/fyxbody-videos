using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FyxBody.Videos.Api;
using FyxBody.Videos.Api.Partner;
using FyxBody.Videos.Api.Portal;
using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FyxBody.Videos.Tests;

public sealed class PartnerPrivateUploadTests
{
    private sealed class Storage : IVideoAssetStorage
    {
        public int Uploaded;
        public int Deleted;

        public Task UploadAsync(VideoAsset asset, Stream content, CancellationToken ct)
        {
            Interlocked.Increment(ref Uploaded);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(VideoAsset asset, CancellationToken ct)
        {
            Interlocked.Increment(ref Deleted);
            return Task.CompletedTask;
        }
    }

    [PostgresFact]
    public async Task ConcurrentUploadsUseOneSlotReplayDoesNotDuplicateAndDeleteFreesQuota()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION"));
        Assert.True(connection.Host is "127.0.0.1" or "localhost");
        Assert.Equal("fyxbody_videos", connection.Database);
        var storage = new Storage();
        await using var app = AdminValidationTests.CreateApplication(connection.ConnectionString).WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Storage:UploadContainer"] = "video", ["Storage:AllowedContainers:0"] = "video" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVideoAssetStorage>();
                services.AddSingleton<IVideoAssetStorage>(storage);
            });
        });
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminValidationTests.AdminKey);
        var owner = new Partner { Code = Guid.NewGuid().ToString("N"), Name = "Private upload test" };
        var outsider = new Partner { Code = Guid.NewGuid().ToString("N"), Name = "Other upload test" };
        var licence = new License { PartnerId = owner.Id, Name = "One private slot", StartsAt = DateTimeOffset.UtcNow.AddDays(-1),
            EndsAt = DateTimeOffset.UtcNow.AddDays(1), AllowPartnerUploads = true, UploadQuotaMode = "videos", UploadQuotaLimit = 1 };
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            db.AddRange(owner, outsider, licence);
            await db.SaveChangesAsync();
        }

        async Task<HttpClient> Client(Guid partnerId)
        {
            using var response = await admin.PostAsJsonAsync($"/admin/v1/partners/{partnerId}/credentials", new { expiresAt = (DateTimeOffset?)null });
            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var client = app.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.RootElement.GetProperty("apiKey").GetString());
            return client;
        }

        using var client = await Client(owner.Id);
        using var other = await Client(outsider.Id);
        var path = $"/v1/licenses/{licence.Id}/private-videos";
        using var first = Request(path, "first");
        using var second = Request(path, "second");
        var responses = await Task.WhenAll(client.SendAsync(first), client.SendAsync(second));
        using var responseA = responses[0];
        using var responseB = responses[1];
        var winner = Assert.Single(responses, response => response.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, Assert.Single(responses, response => !response.IsSuccessStatusCode).StatusCode);
        var video = Assert.IsType<PrivateVideoResponse>(await winner.Content.ReadFromJsonAsync<PrivateVideoResponse>());
        var key = ReferenceEquals(winner, responseA) ? "first" : "second";
        Assert.Equal("owner-note", video.PartnerReference);
        Assert.Equal(1, storage.Uploaded);

        using var replay = Request(path, key);
        using var replayResponse = await client.SendAsync(replay);
        Assert.Equal(video.Id, (await replayResponse.Content.ReadFromJsonAsync<PrivateVideoResponse>())!.Id);
        Assert.Equal(1, storage.Uploaded);
        using var mismatch = Request(path, key, title: "Changed payload");
        using var mismatchResponse = await client.SendAsync(mismatch);
        Assert.Equal(HttpStatusCode.Conflict, mismatchResponse.StatusCode);
        using var otherUpdate = await other.PatchAsJsonAsync($"/v1/private-videos/{video.Id}", new PrivateVideoMetadata("Other title", null));
        Assert.Equal(HttpStatusCode.NotFound, otherUpdate.StatusCode);

        var usagePath = $"/v1/licenses/{licence.Id}/private-video-usage";
        var usage = (await client.GetFromJsonAsync<PrivateVideoUsage>(usagePath))!;
        Assert.Equal(1, usage.VideoCount);
        Assert.Equal(32, usage.StorageBytes);
        using var deleted = await client.DeleteAsync($"/v1/private-videos/{video.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(1, storage.Deleted);
        var after = (await client.GetFromJsonAsync<PrivateVideoUsage>(usagePath))!;
        Assert.Equal(0, after.VideoCount);
        Assert.Equal(0, after.StorageBytes);

        async Task SetQuota(string mode, long? limit)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            var row = await db.Licenses.FindAsync(licence.Id) ?? throw new InvalidOperationException("Test licence missing.");
            row.UploadQuotaMode = mode;
            row.UploadQuotaLimit = limit;
            await db.SaveChangesAsync();
        }

        await SetQuota("storage", 31);
        using var overBytes = Request(path, "storage");
        using var overBytesResponse = await client.SendAsync(overBytes);
        Assert.Equal(HttpStatusCode.Conflict, overBytesResponse.StatusCode);
        Assert.Equal(1, storage.Uploaded);

        await SetQuota("storage", 32);
        using var exactBytes = Request(path, "storage");
        using var exactBytesResponse = await client.SendAsync(exactBytes);
        Assert.Equal(HttpStatusCode.OK, exactBytesResponse.StatusCode);
        await SetQuota("unlimited", null);
        using var unlimited = Request(path, "unlimited");
        using var unlimitedResponse = await client.SendAsync(unlimited);
        Assert.Equal(HttpStatusCode.OK, unlimitedResponse.StatusCode);
        var unlimitedUsage = (await client.GetFromJsonAsync<PrivateVideoUsage>(usagePath))!;
        Assert.Equal(2, unlimitedUsage.VideoCount);
        Assert.Equal(64, unlimitedUsage.StorageBytes);

    }

    [PostgresFact]
    public async Task PortalCookieCanManagePrivateVideosAndDelegateOnlyWithCsrf()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION"));
        Assert.True(connection.Host is "127.0.0.1" or "localhost");
        Assert.Equal("fyxbody_videos", connection.Database);
        var storage = new Storage();
        await using var app = AdminValidationTests.CreateApplication(connection.ConnectionString).WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Storage:UploadContainer"] = "video", ["Storage:AllowedContainers:0"] = "video" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVideoAssetStorage>();
                services.AddSingleton<IVideoAssetStorage>(storage);
            });
        });

        var owner = new Partner { Code = Guid.NewGuid().ToString("N"), Name = "Portal management test" };
        var outsider = new Partner { Code = Guid.NewGuid().ToString("N"), Name = "Portal isolation test" };
        var now = DateTimeOffset.UtcNow;
        var licence = new License { PartnerId = owner.Id, Name = "Portal licence", StartsAt = now.AddDays(-1), EndsAt = now.AddDays(1),
            AllowSubpartnerManagement = true, AllowCatalogueDelegation = true, AllowPartnerUploads = true };
        const string password = "Only-for-tests-portal-management-94";
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<PortalPasswords>();
            db.AddRange(owner, outsider, licence);
            foreach (var partner in new[] { owner, outsider })
            {
                var user = new PortalUser { Username = partner.Code, NormalizedUsername = partner.Code.ToUpperInvariant(),
                    DisplayName = partner.Name, Role = "partner", PartnerId = partner.Id, MustChangePassword = false };
                user.PasswordHash = hasher.Hash(user, password);
                db.PortalUsers.Add(user);
            }
            await db.SaveChangesAsync();
        }

        async Task<string> Login(HttpClient client, string username)
        {
            var before = (await client.GetFromJsonAsync<PortalSessionResponse>("/portal/api/session"))!;
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", before.CsrfToken);
            using var response = await client.PostAsJsonAsync("/portal/api/login", new { username, password });
            response.EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            return (await response.Content.ReadFromJsonAsync<PortalSessionResponse>())!.CsrfToken;
        }

        using var client = app.CreateClient();
        using var other = app.CreateClient();
        var csrf = await Login(client, owner.Code);
        await Login(other, outsider.Code);
        var management = (await client.GetFromJsonAsync<PartnerManagementView>("/v1/management"))!;
        Assert.True(management.CanManageSubpartners);
        Assert.True(management.CanConfigureSubpartnerUploads);
        Assert.Equal(licence.Id, Assert.Single(management.Licenses).Id);
        Assert.False((await other.GetFromJsonAsync<PartnerManagementView>("/v1/management"))!.CanManageSubpartners);

        var uploads = $"/v1/licenses/{licence.Id}/private-videos";
        using var withoutCsrf = Request(uploads, "portal-cookie");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(withoutCsrf)).StatusCode);
        Assert.Equal(0, storage.Uploaded);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
        using var upload = Request(uploads, "portal-cookie");
        using var created = await client.SendAsync(upload);
        created.EnsureSuccessStatusCode();
        var video = (await created.Content.ReadFromJsonAsync<PrivateVideoResponse>())!;
        var list = (await client.GetFromJsonAsync<AdminPage<PrivateVideoResponse>>(uploads))!;
        Assert.Equal(video.Id, Assert.Single(list.Items).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(uploads)).StatusCode);
        var delegable = $"/v1/licenses/{licence.Id}/delegable-videos";
        Assert.Equal(video.Id, Assert.Single((await client.GetFromJsonAsync<CatalogResponse>(delegable))!.Items).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(delegable)).StatusCode);

        var child = new SubpartnerInput(Guid.NewGuid().ToString("N"), "Portal child", "active");
        using var childResponse = await client.PostAsJsonAsync("/v1/subpartners", child);
        Assert.Equal(HttpStatusCode.Created, childResponse.StatusCode);
        var childView = (await childResponse.Content.ReadFromJsonAsync<PartnerView>())!;
        Assert.Equal(owner.Id, childView.ParentPartnerId);

        var privatePath = $"/v1/private-videos/{video.Id}";
        using var edited = await client.PatchAsJsonAsync(privatePath, new PrivateVideoMetadata("Edited in the portal", "portal-reference"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("portal-reference", (await edited.Content.ReadFromJsonAsync<PrivateVideoResponse>())!.PartnerReference);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(privatePath)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<AdminPage<PrivateVideoResponse>>(uploads))!.Items);
    }

    private static HttpRequestMessage Request(string path, string key, string title = "My video")
    {
        var bytes = new byte[32];
        bytes[3] = 24;
        Encoding.ASCII.GetBytes("ftypisom", bytes.AsSpan(4));
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new("video/mp4");
        var form = new MultipartFormDataContent
        {
            { file, "file", "video.mp4" },
            { new StringContent(title), "title" },
            { new StringContent("owner-note"), "partnerReference" },
            { new StringContent("es"), "language" },
            { new StringContent("original"), "rendition" },
            { new StringContent("1000"), "durationMs" }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }
}

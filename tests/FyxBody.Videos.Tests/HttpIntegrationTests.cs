using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FyxBody.Videos.Api;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FyxBody.Videos.Tests;

public sealed class HttpIntegrationTests
{
    private sealed class FakeSigner : IVideoUrlSigner
    {
        public Task<Uri> CreateReadUrlAsync(VideoAsset asset, DateTimeOffset expiresAt, CancellationToken ct) => Task.FromResult(new Uri("https://media.example.invalid/video.mp4?test=permission"));
    }

    [Fact]
    public async Task OpenApiSeparatesPartnerAndAdministrationAndDocumentsSecurity()
    {
        await using var application = Factory(null, "");
        using var client = application.CreateClient();
        using var partner = JsonDocument.Parse(await client.GetStringAsync("/openapi/partner.json"));
        var paths = partner.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/v1/videos", out var catalogue));
        Assert.DoesNotContain(paths.EnumerateObject(), x => x.Name.StartsWith("/admin/", StringComparison.Ordinal));
        Assert.True(catalogue.GetProperty("get").GetProperty("responses").GetProperty("200").TryGetProperty("content", out _));
        Assert.True(catalogue.GetProperty("get").GetProperty("security")[0].TryGetProperty("PartnerKey", out _));
        Assert.Single(paths.GetProperty("/v1/videos/{videoId}/playback-sessions").GetProperty("post").GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "Idempotency-Key" && p.GetProperty("required").GetBoolean());
        using var admin = JsonDocument.Parse(await client.GetStringAsync("/openapi/admin.json"));
        Assert.All(admin.RootElement.GetProperty("paths").EnumerateObject(), path => Assert.StartsWith("/admin/v1/", path.Name));
        var denied = await client.GetAsync("/admin/v1/partners");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var error = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal("invalid_credentials", error.RootElement.GetProperty("code").GetString());
        Assert.True(error.RootElement.TryGetProperty("requestId", out _));
    }

    [PostgresFact]
    public async Task AdministratorCanGrantCatalogueAndPartnerCanPlayReportAndBeRevoked()
    {
        var connection = Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")!;
        var settings = new NpgsqlConnectionStringBuilder(connection);
        Assert.True(settings.Host is "localhost" or "127.0.0.1");
        Assert.Equal("fyxbody_videos", settings.Database);
        var adminKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var application = Factory(connection, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(adminKey))));
        using var admin = application.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", adminKey);
        var suffix = Guid.NewGuid().ToString("N");
        var partner = await Post(admin, "/admin/v1/partners", new { code = suffix, name = "HTTP Test", status = "active" });
        var partnerId = partner.GetProperty("id").GetGuid();
        var createdKey = await Post(admin, $"/admin/v1/partners/{partnerId}/credentials", new { expiresAt = (DateTimeOffset?)null });
        var apiKey = createdKey.GetProperty("apiKey").GetString()!;
        var credentialId = createdKey.GetProperty("credential").GetProperty("id").GetGuid();
        var video = await Post(admin, "/admin/v1/videos", new { externalExerciseId = suffix, title = "HTTP video", status = "active" });
        var videoId = video.GetProperty("id").GetGuid();
        var asset = await Post(admin, $"/admin/v1/videos/{videoId}/assets", new { language = "es", rendition = "720p", blobContainer = "videos", blobName = suffix + ".mp4", contentType = "video/mp4", sizeBytes = 100, durationMs = 38000, status = "ready" });
        var assetId = asset.GetProperty("id").GetGuid();
        var license = await Post(admin, $"/admin/v1/partners/{partnerId}/licenses", new { name = "HTTP license", status = "active", startsAt = DateTimeOffset.UtcNow.AddDays(-1), endsAt = DateTimeOffset.UtcNow.AddDays(1), monthlySessionQuota = 1 });
        var licenseId = license.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/admin/v1/partners/{partnerId}/licenses/{licenseId}/catalogue", new { videoIds = new[] { videoId } })).StatusCode);
        using var partnerClient = application.CreateClient();
        partnerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var catalogue = await partnerClient.GetStringAsync("/v1/videos");
        Assert.Contains(videoId.ToString(), catalogue);
        Assert.DoesNotContain("blobName", catalogue);
        Assert.DoesNotContain("secretHash", catalogue);
        Assert.Equal(HttpStatusCode.Unauthorized, (await partnerClient.GetAsync("/admin/v1/partners")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await partnerClient.GetAsync($"/v1/videos/{Guid.NewGuid()}")).StatusCode);
        var path = $"/v1/videos/{videoId}/playback-sessions";
        Assert.Equal(HttpStatusCode.BadRequest, (await partnerClient.PostAsJsonAsync(path, new { videoAssetId = assetId })).StatusCode);
        partnerClient.DefaultRequestHeaders.Add("Idempotency-Key", "http-1");
        var session = await Post(partnerClient, path, new { videoAssetId = assetId });
        var sessionId = session.GetProperty("sessionId").GetGuid();
        var replay = await Post(partnerClient, path, new { videoAssetId = assetId });
        Assert.Equal(sessionId, replay.GetProperty("sessionId").GetGuid());
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        partnerClient.DefaultRequestHeaders.Remove("Idempotency-Key");
        partnerClient.DefaultRequestHeaders.Add("Idempotency-Key", "http-2");
        var quota = await partnerClient.PostAsJsonAsync(path, new { videoAssetId = assetId });
        Assert.Equal(HttpStatusCode.TooManyRequests, quota.StatusCode);
        Assert.NotNull(quota.Headers.RetryAfter);
        var eventBody = new { events = new[] { new { sessionId, eventKey = "start", kind = "started", positionMs = 0, occurredAt = DateTimeOffset.UtcNow } } };
        Assert.Equal(HttpStatusCode.OK, (await partnerClient.PostAsJsonAsync("/v1/playback-events", eventBody)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await partnerClient.PostAsJsonAsync("/v1/playback-events", eventBody)).StatusCode);
        using var usage = JsonDocument.Parse(await partnerClient.GetStringAsync("/v1/usage"));
        Assert.Equal(1, usage.RootElement.GetProperty("startedSessions").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/admin/v1/partners/{partnerId}/licenses/{licenseId}/catalogue", new { videoIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await partnerClient.GetAsync($"/v1/videos/{videoId}")).StatusCode);
        partnerClient.DefaultRequestHeaders.Remove("Idempotency-Key");partnerClient.DefaultRequestHeaders.Add("Idempotency-Key", "http-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await partnerClient.PostAsJsonAsync(path, new { videoAssetId = assetId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/admin/v1/partners/{partnerId}/credentials/{credentialId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await partnerClient.GetAsync("/v1/videos")).StatusCode);
        using var scope = application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
        var stored = await db.PartnerCredentials.SingleAsync(x => x.Id == credentialId);
        Assert.StartsWith("sha256:", stored.SecretHash);
        Assert.DoesNotContain(apiKey.Split('.')[1], stored.SecretHash);
        Assert.True(await db.PlaybackSessions.AnyAsync(x => x.Id == sessionId));
    }

    [PostgresFact]
    public async Task AnonymousRequestsCannotConsumeAdministratorRateLimit()
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var application = Factory(Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION"), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        using var anonymous = application.CreateClient();
        for (var i = 0; i < 61; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/admin/v1/partners")).StatusCode);
        using var admin = application.CreateClient();admin.DefaultRequestHeaders.Add("X-Admin-Key", key);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/v1/partners")).StatusCode);
    }

    private static WebApplicationFactory<Program> Factory(string? connection, string adminHash) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Videos"] = connection ?? "", ["Administration:ApiKeySha256"] = adminHash
        }));
        builder.ConfigureServices(services => { services.RemoveAll<IVideoUrlSigner>();services.AddSingleton<IVideoUrlSigner, FakeSigner>(); });
    });
    private static async Task<JsonElement> Post(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);return json.RootElement.Clone();
    }
}

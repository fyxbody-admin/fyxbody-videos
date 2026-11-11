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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FyxBody.Videos.Tests;

public sealed class PartnerDelegationTests
{
    private sealed class Signer : IVideoUrlSigner
    {
        public Task<Uri> CreateReadUrlAsync(VideoAsset asset, DateTimeOffset expiresAt, CancellationToken ct) =>
            Task.FromResult(new Uri("https://private.example.invalid/video.mp4"));
    }

    [PostgresFact]
    public async Task PrincipalCanProvisionChildAndDelegatePrivateVideoUntilSourceRevoked()
    {
        var connection = Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")!;
        var settings = new NpgsqlConnectionStringBuilder(connection);
        Assert.True(settings.Host is "localhost" or "127.0.0.1");
        Assert.Equal("fyxbody_videos", settings.Database);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Videos"] = connection,
                ["Administration:ApiKeySha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVideoUrlSigner>();
                services.AddSingleton<IVideoUrlSigner, Signer>();
            });
        });
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", key);
        var principalId = (await Post(admin, "/admin/v1/partners", NewPartner())).GetProperty("id").GetGuid();
        var outsiderId = (await Post(admin, "/admin/v1/partners", NewPartner())).GetProperty("id").GetGuid();
        var principal = app.CreateClient();
        var outsider = app.CreateClient();
        Authenticate(principal, await Post(admin, $"/admin/v1/partners/{principalId}/credentials", new { expiresAt = (DateTimeOffset?)null }));
        Authenticate(outsider, await Post(admin, $"/admin/v1/partners/{outsiderId}/credentials", new { expiresAt = (DateTimeOffset?)null }));

        var source = await Post(admin, $"/admin/v1/partners/{principalId}/licenses", License(true));
        var sourceId = source.GetProperty("id").GetGuid();
        var outsiderLicenseId = (await Post(admin, $"/admin/v1/partners/{outsiderId}/licenses", License(true))).GetProperty("id").GetGuid();
        var childId = (await Post(principal, "/v1/subpartners", NewPartner())).GetProperty("id").GetGuid();
        var childLicense = await Post(principal, $"/v1/subpartners/{childId}/licenses", License(false));
        Assert.Equal("videos", childLicense.GetProperty("uploadQuotaMode").GetString());
        var childLicenseId = childLicense.GetProperty("id").GetGuid();
        using var child = app.CreateClient();
        var childCredential = await Post(principal, $"/v1/subpartners/{childId}/credentials", new { expiresAt = (DateTimeOffset?)null });
        Authenticate(child, childCredential);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1/subpartners/{childId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await child.PostAsJsonAsync("/v1/subpartners", NewPartner())).StatusCode);

        var videoId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            db.AddRange(new Video { Id = videoId, OwnerPartnerId = principalId, UploadLicenseId = sourceId,
                ExternalExerciseId = $"private:{videoId:D}", Title = "Owned", PartnerReference = "owner metadata", Status = "active" },
                new VideoAsset { Id = assetId, VideoId = videoId, BlobContainer = "videos", BlobName = $"{videoId:D}.mp4", Status = "ready", SizeBytes = 500, DurationMs = 10000 },
                new LicenseVideo { LicenseId = sourceId, VideoId = videoId });
            await db.SaveChangesAsync();
        }

        var videoPath = $"/v1/videos/{videoId}";
        Assert.Contains("owner metadata", await principal.GetStringAsync(videoPath));
        Assert.Equal(HttpStatusCode.NotFound, (await child.GetAsync(videoPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"/admin/v1/partners/{outsiderId}/licenses/{outsiderLicenseId}/catalogue",
            new { videoIds = new[] { videoId } })).StatusCode);
        var childCatalogue = $"/v1/subpartners/{childId}/licenses/{childLicenseId}/catalogue";
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync(childCatalogue, new { sourceLicenseId = outsiderLicenseId, allCurrent = true })).StatusCode);
        var grant = await principal.PutAsJsonAsync(childCatalogue, new { sourceLicenseId = sourceId, allCurrent = true });
        Assert.True(grant.IsSuccessStatusCode, await grant.Content.ReadAsStringAsync());
        Assert.Contains(videoId.ToString(), await child.GetStringAsync($"/v1/videos?externalExerciseId={Uri.EscapeDataString($"private:{videoId:D}")}"));
        Assert.DoesNotContain("owner metadata", await child.GetStringAsync(videoPath));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(videoPath)).StatusCode);
        child.DefaultRequestHeaders.Add("Idempotency-Key", "delegated-play");
        outsider.DefaultRequestHeaders.Add("Idempotency-Key", "outside-play");
        var playback = videoPath + "/playback-sessions";
        await Post(child, playback, new { videoAssetId = assetId });
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync(playback, new { videoAssetId = assetId })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/admin/v1/partners/{principalId}/licenses/{sourceId}/catalogue",
            new { videoIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await child.GetAsync(videoPath)).StatusCode);
        Assert.False((await child.PostAsJsonAsync(playback, new { videoAssetId = assetId })).IsSuccessStatusCode);
        var credentialId = childCredential.GetProperty("credential").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await principal.DeleteAsync($"/v1/subpartners/{childId}/credentials/{credentialId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await child.GetAsync("/v1/videos")).StatusCode);
    }

    private static object NewPartner() => new { code = Guid.NewGuid().ToString("N"), name = "Integration", status = "active" };
    private static object License(bool principal) => new
    {
        name = "Integration", status = "active", startsAt = DateTimeOffset.UtcNow.AddHours(-1), endsAt = DateTimeOffset.UtcNow.AddHours(1),
        monthlySessionQuota = 20, allowSubpartnerManagement = principal, allowCatalogueDelegation = principal, allowPartnerUploads = true,
        uploadQuotaMode = principal ? "unlimited" : "videos", uploadQuotaLimit = principal ? (long?)null : 100
    };
    private static void Authenticate(HttpClient client, JsonElement credential) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.GetProperty("apiKey").GetString());
    private static async Task<JsonElement> Post(HttpClient client, string path, object payload)
    {
        using var response = await client.PostAsJsonAsync(path, payload);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }
}

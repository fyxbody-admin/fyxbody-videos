using System.Net;
using System.Net.Http.Json;
using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Api.Partner;
using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FyxBody.Videos.Tests;

public sealed class AdminPostgresTests
{
    [PostgresFact]
    public async Task CatalogueReplacementPreservesSessionReferencesAndAssetLocatorsCannotChangeAfterIssue()
    {
        var connection = Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")!;
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not ("localhost" or "127.0.0.1") || settings.Database != "fyxbody_videos")
            throw new InvalidOperationException("Admin tests require a disposable local fyxbody_videos database.");
        await using var app = AdminValidationTests.CreateApplication(connection);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminValidationTests.AdminKey);
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<VideosDbContext>().Database.MigrateAsync();

        var unique = Guid.NewGuid().ToString("N");
        var partner = await Created<PartnerView>(await client.PostAsJsonAsync("/admin/v1/partners", new PartnerInput(unique, "Test", "active")));
        var other = await Created<PartnerView>(await client.PostAsJsonAsync("/admin/v1/partners", new PartnerInput("other-" + unique, "Other", "active")));
        var video = await Created<VideoView>(await client.PostAsJsonAsync("/admin/v1/videos", new VideoInput(unique, "Exercise", "active")));
        var assetInput = new AssetInput("es", "original", "videos", unique + ".mp4", "video/mp4", 123, 1000, "ready");
        var asset = await Created<AssetView>(await client.PostAsJsonAsync($"/admin/v1/videos/{video.Id}/assets", assetInput));
        var now = DateTimeOffset.UtcNow;
        var licenseInput = new LicenseInput("Standard", "active", now.AddDays(-1), now.AddDays(1), 10);
        var license = await Created<LicenseView>(await client.PostAsJsonAsync($"/admin/v1/partners/{partner.Id}/licenses", licenseInput));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/admin/v1/partners/{other.Id}/licenses/{license.Id}", licenseInput)).StatusCode);
        var catalogue = $"/admin/v1/partners/{partner.Id}/licenses/{license.Id}/catalogue";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(catalogue, new CatalogueInput([video.Id]))).StatusCode);
        var effective = await client.GetFromJsonAsync<AdminPage<VideoView>>($"/admin/v1/partners/{partner.Id}/catalogue");
        Assert.Contains(effective!.Items, x => x.Id == video.Id);

        Guid sessionId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            var session = new PlaybackSession { PartnerId = partner.Id, LicenseId = license.Id, VideoId = video.Id, VideoAssetId = asset.Id,
                IdempotencyKey = unique, RequestHash = new string('a', 64), CreatedAt = now, ExpiresAt = now.AddMinutes(5), UsageMonth = new DateOnly(now.Year, now.Month, 1) };
            sessionId = session.Id; db.PlaybackSessions.Add(session);
            db.UsageMonthly.Add(new UsageMonthly { PartnerId = partner.Id, LicenseId = license.Id, Month = session.UsageMonth, SessionsIssued = 1, UpdatedAt = now });
            db.PlaybackEvents.AddRange(
                new PlaybackEvent { PlaybackSessionId = session.Id, EventKey = "started-1", Kind = "started", OccurredAt = now },
                new PlaybackEvent { PlaybackSessionId = session.Id, EventKey = "started-2", Kind = "started", OccurredAt = now },
                new PlaybackEvent { PlaybackSessionId = session.Id, EventKey = "completed", Kind = "completed", PositionMs = 1000, OccurredAt = now });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(catalogue, new CatalogueInput([]))).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            Assert.NotNull((await db.LicenseVideos.SingleAsync(x => x.LicenseId == license.Id && x.VideoId == video.Id)).RevokedAt);
            Assert.True(await db.PlaybackSessions.AnyAsync(x => x.Id == sessionId));
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(catalogue, new CatalogueInput([video.Id]))).StatusCode);
        using (var scope = app.Services.CreateScope())
            Assert.Null((await scope.ServiceProvider.GetRequiredService<VideosDbContext>().LicenseVideos.SingleAsync(x => x.LicenseId == license.Id && x.VideoId == video.Id)).RevokedAt);
        var assetPath = $"/admin/v1/videos/{video.Id}/assets/{asset.Id}";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(assetPath, assetInput with { BlobName = "new-" + unique + ".mp4" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(assetPath, assetInput with { Status = "disabled" })).StatusCode);
        effective = await client.GetFromJsonAsync<AdminPage<VideoView>>($"/admin/v1/partners/{partner.Id}/catalogue");
        Assert.Empty(effective!.Items);

        var month = $"{now.Year:0000}-{now.Month:00}";
        var usage = await client.GetFromJsonAsync<UsageResponse>($"/admin/v1/partners/{partner.Id}/usage?month={month}");
        Assert.Equal(1, Assert.Single(usage!.Licenses).SessionsIssued);
        Assert.Equal(1, usage.StartedSessions); // two started events for the same session count once
        Assert.Equal(1, usage.CompletedSessions);
        var otherUsage = await client.GetFromJsonAsync<UsageResponse>($"/admin/v1/partners/{other.Id}/usage?month={month}");
        Assert.Empty(otherUsage!.Licenses);
        Assert.Equal(0, otherUsage.StartedSessions);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/admin/v1/partners/{partner.Id}/usage?month=2026-13")).StatusCode);
        var export = await client.GetAsync($"/admin/v1/partners/{partner.Id}/usage.csv?month={month}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        Assert.Contains("attachment", export.Content.Headers.ContentDisposition!.ToString());
        var csv = await export.Content.ReadAsStringAsync();
        Assert.Equal(3, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("\"partner_events\"", csv);
        Assert.Contains(license.Id.ToString(), csv);
        Assert.DoesNotContain("sig=", csv);
        Assert.DoesNotContain("secret", csv);

        var created = await Created<CreatedCredentialView>(await client.PostAsJsonAsync($"/admin/v1/partners/{partner.Id}/credentials", new CredentialInput(now.AddDays(1))));
        Assert.Contains('.', created.ApiKey);
        var metadata = await client.GetStringAsync($"/admin/v1/partners/{partner.Id}/credentials");
        Assert.DoesNotContain(created.ApiKey, metadata);
        Assert.DoesNotContain("secretHash", metadata);
        Assert.DoesNotContain("apiKey", metadata);
        var revoked = await client.DeleteAsync($"/admin/v1/partners/{partner.Id}/credentials/{created.Credential.Id}");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.NotNull((await revoked.Content.ReadFromJsonAsync<CredentialView>())!.RevokedAt);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", created.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/videos")).StatusCode);
    }

    private static async Task<T> Created<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FyxBody.Videos.Api;
using FyxBody.Videos.Api.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FyxBody.Videos.Tests;

public sealed class AdminValidationTests
{
    [Theory]
    [InlineData("https://example.test/video.mp4")]
    [InlineData("exercise.mp4?sig=secret")]
    [InlineData("../exercise.mp4")]
    [InlineData("folder/%2e%2e/exercise.mp4")]
    [InlineData("/exercise.mp4")]
    [InlineData("folder\\exercise.mp4")]
    public void AssetRejectsUrlsQueriesAndNavigation(string name) => Assert.Equal(400, Assert.Throws<ApiException>(() => AdminValidation.Asset(Asset(name))).Status);

    [Theory]
    [InlineData("ab")]
    [InlineData("Videos")]
    [InlineData("video--files")]
    [InlineData("-videos")]
    public void AssetRejectsInvalidAzureContainers(string container) => Assert.Throws<ApiException>(() => AdminValidation.Asset(Asset("exercise.mp4") with { BlobContainer = container }));

    [Fact]
    public void ReadyAssetRequiresPositiveSizeAndDuration()
    {
        Assert.Throws<ApiException>(() => AdminValidation.Asset(Asset("exercise.mp4") with { SizeBytes = 0 }));
        Assert.Throws<ApiException>(() => AdminValidation.Asset(Asset("exercise.mp4") with { DurationMs = 0 }));
        Assert.Throws<ApiException>(() => AdminValidation.Asset(Asset("exercise.mp4") with { ContentType = "text/html" }));
        Assert.Equal("exercise.mp4", AdminValidation.Asset(Asset("exercise.mp4")).BlobName);
    }

    [Fact]
    public void LicenceRequiresUtcOrderedDatesAndNonnegativeQuota()
    {
        var now = DateTimeOffset.UtcNow;
        var license = new LicenseInput("Standard", "active", now, now.AddDays(1), 10);
        Assert.Throws<ApiException>(() => AdminValidation.License(license with { EndsAt = now }));
        Assert.Throws<ApiException>(() => AdminValidation.License(license with { StartsAt = now.ToOffset(TimeSpan.FromHours(1)) }));
        Assert.Throws<ApiException>(() => AdminValidation.License(license with { MonthlySessionQuota = -1 }));
        Assert.Equal(0, AdminValidation.License(license with { MonthlySessionQuota = 0 }).MonthlySessionQuota);
    }

    [Fact]
    public void CatalogueRejectsEmptyUuidDuplicatesAndOversizedReplacement()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ApiException>(() => AdminValidation.Catalogue([id, id]));
        Assert.Throws<ApiException>(() => AdminValidation.Catalogue([Guid.Empty]));
        Assert.Throws<ApiException>(() => AdminValidation.Catalogue(Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray()));
        Assert.Empty(AdminValidation.Catalogue([]));
    }

    [Fact]
    public async Task AdministratorAuthenticationHasNoDefaultOrBypassAndValidationRunsBeforeDatabaseQueries()
    {
        await using var application = CreateApplication("Host=127.0.0.1;Port=1;Database=fyxbody_videos;Username=test;Timeout=1");
        using var client = application.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/v1/partners")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        var response = await client.PostAsJsonAsync("/admin/v1/partners", new PartnerInput("INVALID", "Client", "active"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_request", body);
        Assert.DoesNotContain("database_unavailable", body);
    }

    private static AssetInput Asset(string name) => new("es", "original", "videos", name, "video/mp4", 123, 1000, "ready");
    internal const string AdminKey = "local-tests-administration-credential-2026";
    internal static WebApplicationFactory<Program> CreateApplication(string connection) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Videos"] = connection,
            ["Administration:ApiKeySha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AdminKey)))
        }));
    });
}

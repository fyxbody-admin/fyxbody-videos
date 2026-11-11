using System.Net;
using FyxBody.Videos.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FyxBody.Videos.Tests;

public sealed class ApiFoundationTests
{
    [Fact]
    public async Task LivenessDoesNotRequireDatabaseAndReadinessDoes()
    {
        await using var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Videos"] = "" }));
        });
        using var client = application.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/videos")).StatusCode);
    }

    [Theory]
    [InlineData("Host=localhost;Database=another_database;Username=test")]
    [InlineData("Host=psql-fyxbody2-test-zpfyqf.postgres.database.azure.com;Database=fyxbody_videos;Username=test;SSL Mode=Disable")]
    public void ConfigurationRejectsWrongDatabaseOrUnverifiedAzureTls(string connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Videos"] = connectionString }).Build();
        Assert.Throws<InvalidOperationException>(() => VideoDatabaseConfiguration.GetConnectionString(configuration));
    }
    [Fact]
    public async Task PortalAssetsAreServedLocallyWithSecurityHeaders()
    {
        await using var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = application.CreateClient();
        var page = await client.GetAsync("/portal/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("FYXBODY", await page.Content.ReadAsStringAsync());
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/portal/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/portal/styles.css")).StatusCode);
    }

}

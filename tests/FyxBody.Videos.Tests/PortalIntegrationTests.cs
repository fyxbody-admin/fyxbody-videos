using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FyxBody.Videos.Api;
using FyxBody.Videos.Api.Portal;
using FyxBody.Videos.Api.Partner;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using DataPartner = FyxBody.Videos.Data.Partner;

namespace FyxBody.Videos.Tests;

public sealed class PortalIntegrationTests
{
    private static WebApplicationFactory<Program> Factory(string? key = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:Videos"] = LocalConnection(),
            ["Administration:ApiKeySha256"] = key == null ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
        }));
    });
    private static string LocalConnection()
    {
        var connection = Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")!;
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not ("127.0.0.1" or "localhost") || settings.Database != "fyxbody_videos") throw new InvalidOperationException("A disposable local database is required.");
        return connection;
    }
    private sealed record User(Guid Id, string Username, string Password, Guid? PartnerId);
    private static async Task<User> SeedAsync(WebApplicationFactory<Program> app, string role = "partner", bool mustChange = false)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PortalPasswords>();
        var name = Guid.NewGuid().ToString("N");
        Guid? partnerId = null;
        if (role == "partner")
        {
            var partner = new DataPartner { Code = name, Name = "Portal integration" };
            db.Partners.Add(partner); partnerId = partner.Id;
        }
        var user = new PortalUser { Username = name, NormalizedUsername = name.ToUpperInvariant(), DisplayName = "Portal integration", Role = role, PartnerId = partnerId, MustChangePassword = mustChange };
        const string password = "Only-for-tests-password-73";
        user.PasswordHash = hasher.Hash(user, password);
        db.PortalUsers.Add(user);
        await db.SaveChangesAsync();
        return new(user.Id, name, password, partnerId);
    }
    private static async Task<PortalSessionResponse> RefreshAsync(HttpClient client)
    {
        var session = (await client.GetFromJsonAsync<PortalSessionResponse>("/portal/api/session"))!;
        SetCsrf(client, session.CsrfToken);
        return session;
    }
    private static void SetCsrf(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }
    private static async Task<PortalSessionResponse> LoginAsync(HttpClient client, User user)
    {
        await RefreshAsync(client);
        var response = await client.PostAsJsonAsync("/portal/api/login", new { username = user.Username, password = user.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<PortalSessionResponse>())!;
        SetCsrf(client, session.CsrfToken);
        return session;
    }

    [PostgresFact]
    public async Task LoginRequiresCsrfAndPasswordChangeUnlocksOnlyPartnersOwnApi()
    {
        await using var app = Factory();
        var user = await SeedAsync(app, mustChange: true);
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/portal/api/login", new { username = user.Username, password = user.Password })).StatusCode);
        var session = await LoginAsync(client, user);
        Assert.True(session.User!.MustChangePassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/videos")).StatusCode);
        var password = await client.PostAsJsonAsync("/portal/api/password", new { currentPassword = user.Password, newPassword = "Changed-password-for-tests-84" });
        Assert.Equal(HttpStatusCode.OK, password.StatusCode);
        var changed = (await password.Content.ReadFromJsonAsync<PortalSessionResponse>())!;
        Assert.False(changed.User!.MustChangePassword);
        SetCsrf(client, changed.CsrfToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/videos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/v1/partners")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/portal/api/users")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/v1/playback-events", new { events = Array.Empty<object>() })).StatusCode);
        await RefreshAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/portal/api/logout", null)).StatusCode);
        Assert.Null((await RefreshAsync(client)).User);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/videos")).StatusCode);
    }

    [PostgresFact]
    public async Task PasswordChangeInvalidatesOtherCookiesAndResetInvalidatesCurrentCookie()
    {
        await using var app = Factory();
        var user = await SeedAsync(app);
        var admin = await SeedAsync(app, "admin");
        using var first = app.CreateClient(); using var other = app.CreateClient(); using var administrator = app.CreateClient();
        await LoginAsync(first, user); await LoginAsync(other, user); await LoginAsync(administrator, admin);
        var response = await first.PostAsJsonAsync("/portal/api/password", new { currentPassword = user.Password, newPassword = "Another-password-for-tests-95" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await RefreshAsync(other)).User);
        Assert.NotNull((await RefreshAsync(first)).User);
        var reset = await administrator.PostAsync($"/portal/api/users/{user.Id}/reset-password", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var resetBody = (await reset.Content.ReadFromJsonAsync<PortalUserCreatedResponse>())!;
        Assert.NotNull(resetBody.TemporaryPassword);
        Assert.True(resetBody.User.MustChangePassword);
        Assert.Null((await RefreshAsync(first)).User);
        using var fresh = app.CreateClient();
        var session = await LoginAsync(fresh, user with { Password = resetBody.TemporaryPassword! });
        Assert.True(session.User!.MustChangePassword);
    }

    [PostgresFact]
    public async Task ApiKeysCannotManagePortalUsersAndAdminCannotDisableSelf()
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var app = Factory(key);
        var admin = await SeedAsync(app, "admin");
        using var keyClient = app.CreateClient(); keyClient.DefaultRequestHeaders.Add("X-Admin-Key", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/portal/api/users")).StatusCode);
        using var client = app.CreateClient(); await LoginAsync(client, admin);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/portal/api/users/{admin.Id}", new { displayName = "Admin", isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/portal/api/users")).StatusCode);
    }

    [PostgresFact]
    public async Task AdminCreatesTemporaryPasswordUserAndDisablingRevokesCookie()
    {
        await using var app = Factory();
        var admin = await SeedAsync(app, "admin");
        var partnerOwner = await SeedAsync(app);
        using var administrator = app.CreateClient();
        await LoginAsync(administrator, admin);
        var username = "Temp-" + Guid.NewGuid().ToString("N");
        var response = await administrator.PostAsJsonAsync("/portal/api/users", new { username, displayName = "<script>literal</script>", role = "partner", partnerId = partnerOwner.PartnerId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<PortalUserCreatedResponse>())!;
        Assert.NotNull(created.TemporaryPassword);
        Assert.True(created.User.MustChangePassword);
        Assert.DoesNotContain("passwordHash", await response.Content.ReadAsStringAsync());
        var duplicate = await administrator.PostAsJsonAsync("/portal/api/users", new { username = username.ToUpperInvariant(), displayName = "Duplicate", role = "partner", partnerId = partnerOwner.PartnerId });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var userClient = app.CreateClient();
        await LoginAsync(userClient, new User(created.User.Id, username, created.TemporaryPassword!, partnerOwner.PartnerId));
        Assert.NotNull((await RefreshAsync(userClient)).User);
        var disable = await administrator.PutAsJsonAsync($"/portal/api/users/{created.User.Id}", new { displayName = "Disabled", isActive = false });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Null((await RefreshAsync(userClient)).User);
        var listed = (await administrator.GetFromJsonAsync<PortalUserPage>("/portal/api/users?page=1&pageSize=100"))!;
        Assert.DoesNotContain("passwordHash", System.Text.Json.JsonSerializer.Serialize(listed));
    }

    [PostgresFact]
    public async Task PartnerDeactivationInvalidatesPortalCookie()
    {
        await using var app = Factory();
        var user = await SeedAsync(app);
        using var client = app.CreateClient(); await LoginAsync(client, user);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<VideosDbContext>().Partners.Where(p => p.Id == user.PartnerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "suspended"));
        Assert.Null((await RefreshAsync(client)).User);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/videos")).StatusCode);
    }

    [PostgresFact]
    public async Task FiveFailuresLockAccountEvenWithCorrectPasswordAndErrorsAreGeneric()
    {
        await using var app = Factory();
        var user = await SeedAsync(app);
        using var client = app.CreateClient(); await RefreshAsync(client);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/portal/api/login", new { username = user.Username, password = "wrong-password" })).StatusCode);
        var locked = await client.PostAsJsonAsync("/portal/api/login", new { username = user.Username, password = user.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        var unknown = await client.PostAsJsonAsync("/portal/api/login", new { username = Guid.NewGuid().ToString("N"), password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var lockedText = await locked.Content.ReadAsStringAsync();
        Assert.Contains("invalid_login", lockedText);
        Assert.DoesNotContain("lockout", lockedText, StringComparison.OrdinalIgnoreCase);
        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<VideosDbContext>().PortalUsers.SingleAsync(u => u.Id == user.Id);
        Assert.Equal(5, stored.FailedLoginCount);
        Assert.NotNull(stored.LockoutUntil);
        Assert.DoesNotContain(user.Password, stored.PasswordHash);
    }
    [PostgresFact]
    public async Task PortalPartnerSeesOnlyOwnCatalogueAndConsumption()
    {
        await using var app = Factory();
        var own = await SeedAsync(app); var other = await SeedAsync(app);
        var now = DateTimeOffset.UtcNow;
        var ownVideo = new Video { ExternalExerciseId = Guid.NewGuid().ToString("N"), Title = "OWN", Status = "active" };
        var otherVideo = new Video { ExternalExerciseId = Guid.NewGuid().ToString("N"), Title = "OTHER", Status = "active" };
        var ownLicense = new License { PartnerId = own.PartnerId!.Value, Name = "Own", StartsAt = now.AddDays(-1), EndsAt = now.AddDays(1), MonthlySessionQuota = 10 };
        var otherLicense = new License { PartnerId = other.PartnerId!.Value, Name = "Other", StartsAt = now.AddDays(-1), EndsAt = now.AddDays(1), MonthlySessionQuota = 100 };
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            db.AddRange(ownVideo, otherVideo, ownLicense, otherLicense,
                new VideoAsset { VideoId = ownVideo.Id, BlobContainer = "videos", BlobName = Guid.NewGuid()+".mp4", Status = "ready", DurationMs = 38000, SizeBytes = 100 },
                new VideoAsset { VideoId = otherVideo.Id, BlobContainer = "videos", BlobName = Guid.NewGuid()+".mp4", Status = "ready", DurationMs = 38000, SizeBytes = 100 },
                new LicenseVideo { LicenseId = ownLicense.Id, VideoId = ownVideo.Id }, new LicenseVideo { LicenseId = otherLicense.Id, VideoId = otherVideo.Id },
                new UsageMonthly { PartnerId = own.PartnerId.Value, LicenseId = ownLicense.Id, Month = new DateOnly(now.Year, now.Month, 1), SessionsIssued = 7 },
                new UsageMonthly { PartnerId = other.PartnerId.Value, LicenseId = otherLicense.Id, Month = new DateOnly(now.Year, now.Month, 1), SessionsIssued = 99 });
            await db.SaveChangesAsync();
        }
        using var client = app.CreateClient();await LoginAsync(client, own);
        var catalogue = (await client.GetFromJsonAsync<CatalogResponse>($"/v1/videos?partnerId={other.PartnerId}"))!;
        Assert.Equal(ownVideo.Id, Assert.Single(catalogue.Items).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1/videos/{otherVideo.Id}")).StatusCode);
        var usage = (await client.GetFromJsonAsync<UsageResponse>("/v1/usage"))!;
        Assert.Equal(7, Assert.Single(usage.Licenses).SessionsIssued);
    }

    private sealed class UnusedSigner : IVideoUrlSigner
    {
        public Task<Uri> CreateReadUrlAsync(VideoAsset asset, DateTimeOffset expiresAt, CancellationToken ct) => throw new InvalidOperationException("Not used by catalogue tests.");
    }

    [PostgresFact]
    public async Task PartnerServiceRejectsPortalIdentityAfterSecurityStampChanges()
    {
        await using var app = Factory();var user = await SeedAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
        var stored = await db.PortalUsers.SingleAsync(x => x.Id == user.Id);
        var stale = new PartnerIdentity(user.PartnerId!.Value, Guid.Empty, user.Id, stored.SecurityStamp);
        stored.SecurityStamp = Guid.NewGuid();await db.SaveChangesAsync();
        var service = new PartnerVideoService(db, new UnusedSigner(), TimeProvider.System, new ConfigurationBuilder().Build());
        var rejected = await Assert.ThrowsAsync<ApiException>(() => service.CatalogAsync(stale, 1, 25, default));
        Assert.Equal(401, rejected.Status);
    }

}

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FyxBody.Videos.Api;
using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Api.Localization;
using FyxBody.Videos.Api.Portal;
using FyxBody.Videos.Api.YouTube;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FyxBody.Videos.Tests;

public sealed class BackendLocalizationTests
{
    private static WebApplicationFactory<Program> App(bool database = false) => AdminValidationTests.CreateApplication(database ? Connection() : "Host=127.0.0.1;Port=1;Database=fyxbody_videos;Username=test;Timeout=1");
    private static string Connection()
    {
        var connection = Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")!; var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not ("127.0.0.1" or "localhost") || settings.Database != "fyxbody_videos") throw new InvalidOperationException("A disposable local database is required."); return connection;
    }
    private static void Language(HttpClient client, string language)
    { client.DefaultRequestHeaders.Remove("Accept-Language"); client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", language); }
    private static void Headers(HttpResponseMessage response, string language)
    {
        Assert.Equal(language, Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Contains(response.Headers.Vary, value => value.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase));
    }
    private static async Task<JsonElement> Problem(HttpResponseMessage response, string language, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode); Headers(response, language); Assert.True(response.Headers.CacheControl!.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var error = body.RootElement;
        Assert.Equal(code, error.GetProperty("code").GetString()); Assert.Equal((int)status, error.GetProperty("status").GetInt32()); Assert.True(error.TryGetProperty("requestId", out _)); return error.Clone();
    }

    [Theory]
    [InlineData("es", "es", "Inicia sesión para continuar.")]
    [InlineData("ca", "ca", "Inicia la sessió per continuar.")]
    [InlineData("en", "en", "Sign in to continue.")]
    [InlineData("ar", "ar", "سجّل الدخول للمتابعة.")]
    [InlineData("ar-SA", "ar", "سجّل الدخول للمتابعة.")]
    [InlineData("ca-ES", "ca", "Inicia la sessió per continuar.")]
    [InlineData("en-GB", "en", "Sign in to continue.")]
    [InlineData("fr-FR,ca-ES;q=0.8,en;q=0.5", "ca", "Inicia la sessió per continuar.")]
    [InlineData("en;q=0.1,ar-SA;q=0.9", "ar", "سجّل الدخول للمتابعة.")]
    [InlineData("ar;q=0,en;q=0.5", "en", "Sign in to continue.")]
    [InlineData("ar;q=0", "es", "Inicia sesión para continuar.")]
    [InlineData("de-DE,fr;q=0.8", "es", "Inicia sesión para continuar.")]
    [InlineData("", "es", "Inicia sesión para continuar.")]
    public async Task NegotiationLocalizesAuthenticationAndPreservesProtocol(string header, string language, string title)
    {
        await using var app = App(); using var client = app.CreateClient(); Language(client, header);
        var problem = await Problem(await client.GetAsync("/admin/v1/partners"), language, HttpStatusCode.Unauthorized, "invalid_credentials");
        Assert.Equal(title, problem.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("es", "monthlySessionQuota debe ser mayor o igual a cero.")]
    [InlineData("ca", "monthlySessionQuota ha de ser superior o igual a zero.")]
    [InlineData("en", "monthlySessionQuota must be greater than or equal to zero.")]
    [InlineData("ar", "يجب أن تكون monthlySessionQuota أكبر من أو تساوي صفرًا.")]
    public async Task DifferentValidationDetailsRemainDistinctInEveryLanguage(string language, string quotaTitle)
    {
        await using var app = App(); using var client = app.CreateClient(); Language(client, language); client.DefaultRequestHeaders.Add("X-Admin-Key", AdminValidationTests.AdminKey);
        var now = DateTimeOffset.UtcNow;
        var quota = await Problem(await client.PostAsJsonAsync($"/admin/v1/partners/{Guid.NewGuid()}/licenses", new LicenseInput("Test", "active", now, now.AddDays(1), -1)), language, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(quotaTitle, quota.GetProperty("title").GetString());
        var code = await Problem(await client.PostAsJsonAsync("/admin/v1/partners", new PartnerInput("INVALID", "Test", "active")), language, HttpStatusCode.BadRequest, "invalid_request");
        var name = await Problem(await client.PostAsJsonAsync("/admin/v1/partners", new PartnerInput("valid-code", "", "active")), language, HttpStatusCode.BadRequest, "invalid_request");
        Assert.NotEqual(quota.GetProperty("title").GetString(), code.GetProperty("title").GetString()); Assert.NotEqual(code.GetProperty("title").GetString(), name.GetProperty("title").GetString());
        Assert.Contains("name", name.GetProperty("title").GetString()); Assert.Contains("200", name.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("es")]
    [InlineData("ca")]
    [InlineData("en")]
    [InlineData("ar")]
    public async Task Generic404AndSuccessHeadersUseTheRequestedLanguage(string language)
    {
        await using var app = App(); using var client = app.CreateClient(); Language(client, language);
        await Problem(await client.GetAsync("/path-that-does-not-exist"), language, HttpStatusCode.NotFound, "not_found");
        var root = await client.GetAsync("/"); Assert.Equal(HttpStatusCode.OK, root.StatusCode); Headers(root, language);
        using var json = JsonDocument.Parse(await root.Content.ReadAsStringAsync()); Assert.Equal("v1", json.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void EveryCatalogueEntryHasFourTranslationsAndMatchingPlaceholders()
    {
        using var stream = typeof(ApiMessageCatalog).Assembly.GetManifestResourceStream("FyxBody.Videos.Api.Localization.messages.json")!;
        using var json = JsonDocument.Parse(stream); Assert.True(json.RootElement.EnumerateObject().Count() >= 130);
        foreach (var entry in json.RootElement.EnumerateObject())
        {
            var source = entry.Value.GetProperty("source").GetString()!;
            var placeholders = Regex.Matches(source, @"\{([A-Za-z][A-Za-z0-9]*)\}").Select(match => match.Groups[1].Value).Order().ToArray();
            foreach (var language in new[] { "es", "ca", "en", "ar" })
            {
                var value = entry.Value.GetProperty(language).GetString(); Assert.False(string.IsNullOrWhiteSpace(value), entry.Name + ":" + language);
                Assert.Equal(placeholders, Regex.Matches(value!, @"\{([A-Za-z][A-Za-z0-9]*)\}").Select(match => match.Groups[1].Value).Order().ToArray());
            }
        }
    }

    [Fact]
    public void AllExplicitApiMessagesHaveCatalogueEntriesAndUnknownTextCannotLeak()
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/FyxBody.Videos.Api"));
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories).Where(path => !path.Contains("/obj/") && !path.Contains("/bin/") && !path.Contains("/Localization/")))
        foreach (var line in File.ReadLines(file))
        {
            if (!line.Contains("new ApiException(") && !line.Contains("throw Invalid(\"") && !line.Contains("ApiErrors.WriteAsync(")) continue;
            foreach (Match match in Regex.Matches(line, "\"([^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\""))
            {
                var message = match.Groups[1].Value;
                if (!message.Contains(' ') || !message.EndsWith('.') || message.Contains('{')) continue;
                if (!ApiMessageCatalog.CanTranslate(message)) missing.Add(Path.GetFileName(file) + ": " + message);
            }
        }
        Assert.Empty(missing);
        var before = CultureInfo.CurrentUICulture;
        try { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar"); Assert.Equal("تخزين الفيديو غير متاح مؤقتًا.", ApiMessageCatalog.Translate("secret-diagnostic-token", "video_storage_unavailable")); }
        finally { CultureInfo.CurrentUICulture = before; }
    }

    [PostgresFact]
    public async Task PersistedJobErrorsLocalizeOnReadWithoutChangingStoredTextOrData()
    {
        await using var app = App(database: true); var id = Guid.NewGuid(); const string stored = "No se pudo completar la importación. Consulta el código de error y vuelve a intentarlo.";
        var now = DateTimeOffset.UtcNow;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            db.VideoImportJobs.Add(new VideoImportJob { Id = id, YoutubeId = "WhpknPP0xKU", SourceUrl = "https://www.youtube.com/watch?v=WhpknPP0xKU", Title = "Customer title يبقى", Status = "failed", Stage = "failed", RequestedHeights = [360], CompletedHeights = [], ErrorCode = "import_duration_limit", ErrorMessage = stored, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        try
        {
            using var client = app.CreateClient(); client.DefaultRequestHeaders.Add("X-Admin-Key", AdminValidationTests.AdminKey); var titles = new HashSet<string>();
            foreach (var language in new[] { "es", "ca", "en", "ar" })
            {
                Language(client, language); var response = await client.GetAsync("/admin/v1/video-imports/" + id); Headers(response, language);
                var job = (await response.Content.ReadFromJsonAsync<VideoImportView>())!; Assert.Equal("import_duration_limit", job.ErrorCode); Assert.Equal("failed", job.Status); Assert.Equal("Customer title يبقى", job.Title);
                Assert.Equal("https://www.youtube.com/watch?v=WhpknPP0xKU", job.SourceUrl); titles.Add(job.ErrorMessage!);
            }
            Assert.Equal(4, titles.Count);
            await using var check = app.Services.CreateAsyncScope(); var db = check.ServiceProvider.GetRequiredService<VideosDbContext>(); Assert.Equal(stored, (await db.VideoImportJobs.SingleAsync(job => job.Id == id)).ErrorMessage);
            await db.VideoImportJobs.Where(job => job.Id == id).ExecuteUpdateAsync(update => update.SetProperty(job => job.ErrorMessage, "secret-token diagnostic output"));
            Language(client, "ar"); var legacy = (await client.GetFromJsonAsync<VideoImportView>("/admin/v1/video-imports/" + id))!; Assert.DoesNotContain("secret", legacy.ErrorMessage!); Assert.Contains("مدة", legacy.ErrorMessage!);
        }
        finally { await using var cleanup = app.Services.CreateAsyncScope(); await cleanup.ServiceProvider.GetRequiredService<VideosDbContext>().VideoImportJobs.Where(job => job.Id == id).ExecuteDeleteAsync(); }
    }

    [PostgresFact]
    public async Task ArabicCookieSessionsRetainCsrfRequirementsAndCultureCanChangeWithoutLogin()
    {
        await using var app = App(database: true); var username = Guid.NewGuid().ToString("N"); const string password = "Localization-test-password-73";
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>(); var user = new PortalUser { Username = username, NormalizedUsername = username.ToUpperInvariant(), DisplayName = "اسم العميل", Role = "admin", MustChangePassword = false };
            user.PasswordHash = scope.ServiceProvider.GetRequiredService<PortalPasswords>().Hash(user, password); db.PortalUsers.Add(user); await db.SaveChangesAsync();
        }
        using var client = app.CreateClient(); Language(client, "ar-SA");
        await Problem(await client.PostAsJsonAsync("/portal/api/login", new { username, password }), "ar", HttpStatusCode.BadRequest, "invalid_csrf");
        var session = (await client.GetFromJsonAsync<PortalSessionResponse>("/portal/api/session"))!; client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.CsrfToken);
        var login = await client.PostAsJsonAsync("/portal/api/login", new { username, password }); Headers(login, "ar"); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        session = (await login.Content.ReadFromJsonAsync<PortalSessionResponse>())!; Assert.Equal("اسم العميل", session.User!.DisplayName);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); Language(client, "ca-ES");
        var csrf = await Problem(await client.PostAsJsonAsync("/admin/v1/partners", new PartnerInput("test", "Test", "active")), "ca", HttpStatusCode.BadRequest, "invalid_csrf"); Assert.Contains("seguretat", csrf.GetProperty("title").GetString());
        var current = await client.GetAsync("/portal/api/session"); Headers(current, "ca"); Assert.NotNull((await current.Content.ReadFromJsonAsync<PortalSessionResponse>())!.User);
    }
}

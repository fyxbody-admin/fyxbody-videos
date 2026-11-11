using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using FyxBody.Videos.Api;
using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Api.Localization;
using FyxBody.Videos.Api.Partner;
using FyxBody.Videos.Api.Portal;
using FyxBody.Videos.Api.YouTube;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var bootstrapAdmin = args.Contains("--bootstrap-admin", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(x => x != "--bootstrap-admin").ToArray());
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 262144);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("code", "invalid_request");
    context.ProblemDetails.Extensions.TryAdd("requestId", context.HttpContext.TraceIdentifier);
    var code = context.ProblemDetails.Extensions["code"]?.ToString();
    context.ProblemDetails.Title = ApiMessageCatalog.Translate(context.ProblemDetails.Title, code);
    if (context.ProblemDetails.Detail is not null) context.ProblemDetails.Detail = ApiMessageCatalog.Translate(context.ProblemDetails.Detail, code);
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddApiRequestLocalization();
foreach (var name in new[] { "v1", "partner", "admin", "portal" })
    builder.Services.AddOpenApi(name, options => ApiOpenApi.Configure(options, name));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PreAuthenticationLimiter>();
builder.Services.AddSingleton<IVideoUrlSigner, AzureVideoUrlSigner>();
builder.Services.AddSingleton<IVideoAssetStorage, AzureVideoAssetStorage>();
builder.Services.AddSingleton<VideoUploadSlots>();
builder.Services.AddScoped<AdminVideoUploadService>();
builder.Services.AddYouTubeMediaProcessing();
builder.Services.AddScoped<YouTubeImportQueue>();
builder.Services.AddScoped<YouTubeImportRunner>();
builder.Services.AddHostedService<YouTubeImportWorker>();
builder.Services.AddScoped<PartnerVideoService>();
builder.Services.AddScoped<PartnerVideoUploadService>();
builder.Services.AddDbContext<VideosDbContext>(options =>
{
    var connectionString = VideoDatabaseConfiguration.GetConnectionString(builder.Configuration);
    if (connectionString is null)
        throw new InvalidOperationException("Configure ConnectionStrings__Videos before accessing PostgreSQL.");
    options.UseNpgsql(connectionString, postgres =>
    {
        postgres.SetPostgresVersion(16, 0);
        postgres.MigrationsHistoryTable("__EFMigrationsHistory", "videos");
        postgres.CommandTimeout(30);
    });
});
builder.Services.AddDataProtection().SetApplicationName("FyxBody.Videos.Portal");
builder.Services.AddPortalServices(builder.Configuration, builder.Environment);
builder.Services.AddAuthentication("PartnerKey")
    .AddScheme<AuthenticationSchemeOptions, PartnerKeyHandler>("PartnerKey", _ => { })
    .AddScheme<AuthenticationSchemeOptions, AdministratorKeyHandler>("AdminKey", _ => { })
    .AddPolicyScheme("PartnerAccess", null, options => options.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey("Authorization") ? "PartnerKey" : "PortalCookie")
    .AddPolicyScheme("AdminAccess", null, options => options.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey("X-Admin-Key") ? "AdminKey" : "PortalCookie");
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("portal", policy => policy.AddAuthenticationSchemes("PortalCookie").RequireAuthenticatedUser());
    options.AddPolicy("portal-admin", policy => policy.AddAuthenticationSchemes("PortalCookie").RequireAuthenticatedUser().RequireRole("admin").RequireAssertion(context => context.User.FindFirstValue("must_change_password") != "true"));
    options.AddPolicy("partner", policy => policy.AddAuthenticationSchemes("PartnerAccess").RequireAuthenticatedUser().RequireRole("partner").RequireAssertion(context => context.User.FindFirstValue("must_change_password") != "true"));
    options.AddPolicy("admin", policy => policy.AddAuthenticationSchemes("AdminAccess").RequireAuthenticatedUser().RequireRole("admin").RequireAssertion(context => context.User.FindFirstValue("must_change_password") != "true"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("portal-login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    foreach (var policy in new[] { "partner", "admin" })
    {
        var limit = policy == "partner" ? 300 : 60;
        options.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous", _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    }
    options.OnRejected = async (context, _) =>
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 60;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await ApiErrors.WriteAsync(context.HttpContext, 429, "rate_limit_exceeded", "Too many requests; retry after the indicated delay.");
    };
});
builder.Services.AddHealthChecks().AddCheck<VideoDatabaseHealthCheck>("postgresql", tags: ["ready"]);

var app = builder.Build();
if (bootstrapAdmin)
{
    await PortalBootstrap.CreateAdminAsync(app.Services, app.Configuration, CancellationToken.None);
    Console.WriteLine("Administrator created. No password was logged.");
    return;
}
if (app.Configuration.GetValue<bool>("SearchIndexing:Disabled"))
{
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            return Task.CompletedTask;
        });
        await next();
    });
}

app.UseApiRequestLocalization();
app.UseExceptionHandler();
var storageAccount = app.Configuration["Storage:AccountName"] ?? "";
var mediaSources = System.Text.RegularExpressions.Regex.IsMatch(storageAccount, @"\A[a-z0-9]{3,24}\z")
    ? $"'self' blob: https://{storageAccount}.blob.core.windows.net" : "'self' blob:";
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = $"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; media-src {mediaSources}; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/portal")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.Use(async (context, next) =>
{
    var portalHost = app.Configuration["Portal:PublicHost"];
    var isPortalHome = context.Request.Path == "/" && !string.IsNullOrWhiteSpace(portalHost)
        && string.Equals(context.Request.Host.Host, portalHost, StringComparison.OrdinalIgnoreCase);
    if (isPortalHome || string.Equals(context.Request.Path.Value, "/portal", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/portal/");
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseStatusCodePages(async context =>
{
    var status = context.HttpContext.Response.StatusCode;
    await ApiErrors.WriteAsync(context.HttpContext, status, status == 404 ? "not_found" : "invalid_request", "The request could not be completed.");
});
if (app.Environment.IsDevelopment()) app.MapOpenApi();
else app.UseHttpsRedirection();
// Global abuse protection before authentication, endpoint policies after it.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
    if (context.Request.Path.StartsWithSegments("/v1") || context.Request.Path.StartsWithSegments("/admin"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseMiddleware<PreAuthenticationLimiterMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<PortalCsrfMiddleware>();
app.MapGet("/", () => Results.Ok(new { service = "FYXBODY Videos API", version = "v1" }));
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapPortalEndpoints();
app.MapPartnerEndpoints();
app.MapPartnerDelegationEndpoints();
app.MapPartnerUploadEndpoints();
app.MapAdminEndpoints();
app.Run();

public partial class Program;

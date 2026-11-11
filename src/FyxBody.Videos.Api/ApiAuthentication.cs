using System.Security.Claims;
using System.Text.Encodings.Web;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace FyxBody.Videos.Api;

public sealed class PartnerKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IServiceProvider services, TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.Length == 0) return AuthenticateResult.NoResult();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || header.Length > 256)
            return AuthenticateResult.Fail("Invalid credentials.");
        var parts = header[7..].Split('.');
        if (parts.Length != 2 || parts[0].Length is < 1 or > 128 || parts[1].Length is < 32 or > 128)
            return AuthenticateResult.Fail("Invalid credentials.");
        var db = services.GetRequiredService<VideosDbContext>();
        var credential = await db.PartnerCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.KeyId == parts[0], Context.RequestAborted);
        var now = clock.GetUtcNow();
        if (credential is null || !CredentialSecrets.Verify(parts[1], credential.SecretHash) || credential.RevokedAt is not null || credential.ExpiresAt <= now)
            return AuthenticateResult.Fail("Invalid credentials.");
        if (!await db.Partners.AnyAsync(x => x.Id == credential.PartnerId && x.Status == "active", Context.RequestAborted))
            return AuthenticateResult.Fail("Invalid credentials.");
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, credential.PartnerId.ToString()),
            new Claim("credential_id", credential.Id.ToString()), new Claim(ClaimTypes.Role, "partner")], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return ApiErrors.WriteAsync(Context, 401, "invalid_credentials", "Valid partner credentials are required.");
    }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => ApiErrors.WriteAsync(Context, 403, "forbidden", "Access is forbidden.");
}

public sealed class AdministratorKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers["X-Admin-Key"];
        var configured = configuration["Administration:ApiKeySha256"] ?? "";
        if (header.Count != 1 || !CredentialSecrets.VerifyAdministrator(header.ToString(), configured))
            return Task.FromResult(AuthenticateResult.Fail("Invalid credentials."));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "fyxbody-administrator"), new Claim(ClaimTypes.Role, "admin")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => ApiErrors.WriteAsync(Context, 401, "invalid_credentials", "Valid administrator credentials are required.");
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => ApiErrors.WriteAsync(Context, 403, "forbidden", "Access is forbidden.");
}

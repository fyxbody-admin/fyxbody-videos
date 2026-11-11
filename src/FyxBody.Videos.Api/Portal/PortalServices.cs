using System.Security.Claims;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FyxBody.Videos.Api.Portal;

public static class PortalServices
{
    public const string Scheme = "PortalCookie";
    public static IServiceCollection AddPortalServices(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<IPasswordHasher<PortalUser>, PasswordHasher<PortalUser>>();
        services.AddSingleton<PortalPasswords>();
        services.AddScoped<PortalUserService>();
        services.AddAntiforgery(options => {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = environment.IsDevelopment() ? "fyxbody.portal.csrf" : "__Host-fyxbody.portal.csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        services.AddAuthentication().AddCookie(Scheme, options => {
            options.Cookie.Name = environment.IsDevelopment() ? "fyxbody.portal" : "__Host-fyxbody.portal";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => ApiErrors.WriteAsync(context.HttpContext, 401, "invalid_credentials", "Sign in to continue.");
            options.Events.OnRedirectToAccessDenied = context => ApiErrors.WriteAsync(context.HttpContext, 403, "forbidden", "Access is forbidden.");
            options.Events.OnValidatePrincipal = async context => {
                var id = context.Principal?.FindFirstValue("portal_user_id");
                var stamp = context.Principal?.FindFirstValue("security_stamp");
                if (!Guid.TryParse(id, out var userId) || !Guid.TryParse(stamp, out var securityStamp))
                { context.RejectPrincipal(); return; }
                var db = context.HttpContext.RequestServices.GetRequiredService<VideosDbContext>();
                var user = await db.PortalUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive && u.SecurityStamp == securityStamp, context.HttpContext.RequestAborted);
                if (user == null || (user.Role == "partner" && !await db.Partners.AsNoTracking().AnyAsync(p => p.Id == user.PartnerId && p.Status == "active", context.HttpContext.RequestAborted)))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(Scheme);
                }
            };
        });
        return services;
    }

    public static ClaimsPrincipal Principal(PortalUser user) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, (user.PartnerId ?? user.Id).ToString()),
        new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role),
        new Claim("portal_user_id", user.Id.ToString()), new Claim("security_stamp", user.SecurityStamp.ToString()),
        new Claim("credential_id", Guid.Empty.ToString()), new Claim("must_change_password", user.MustChangePassword ? "true" : "false")
    ], Scheme));

    public static async Task SignInAsync(HttpContext context, PortalUser user)
    {
        var principal = Principal(user);
        await context.SignInAsync(Scheme, principal, new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
        context.User = principal;
    }

    public static async Task ValidateCsrfAsync(HttpContext context)
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { throw new ApiException(400, "invalid_csrf", "Refresh the page and retry the request."); }
    }

    public static PortalSessionResponse Session(HttpContext context, PortalUser? user)
    {
        var token = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken
            ?? throw new InvalidOperationException("Antiforgery token is unavailable.");
        context.Response.Headers.CacheControl = "no-store";
        return new(user == null ? null : PortalUserService.Response(user), token);
    }
}

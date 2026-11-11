using Microsoft.AspNetCore.Authentication;

namespace FyxBody.Videos.Api.Portal;

public static class PortalEndpoints
{
    public static IEndpointRouteBuilder MapPortalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/portal/api").WithGroupName("portal").WithTags("Portal");
        group.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            var cookie = await context.HttpContext.AuthenticateAsync(PortalServices.Scheme);
            if (cookie.Succeeded) context.HttpContext.User = cookie.Principal!;
            var method = context.HttpContext.Request.Method;
            if (method is "POST" or "PUT" or "PATCH" or "DELETE")
                await PortalServices.ValidateCsrfAsync(context.HttpContext);
            return await next(context);
        });
        group.MapGet("/session", async (HttpContext context, PortalUserService users, CancellationToken ct) => {
            var cookie = await context.AuthenticateAsync(PortalServices.Scheme);
            context.User = cookie.Succeeded ? cookie.Principal! : new System.Security.Claims.ClaimsPrincipal();
            var user = cookie.Succeeded ? await users.CurrentAsync(context.User, ct) : null;
            return TypedResults.Ok(PortalServices.Session(context, user));
        }).AllowAnonymous();
        group.MapPost("/login", async (PortalLoginRequest input, HttpContext context, PortalUserService users, CancellationToken ct) => {
            var user = await users.LoginAsync(input, ct);
            await PortalServices.SignInAsync(context, user);
            return TypedResults.Ok(PortalServices.Session(context, user));
        }).AllowAnonymous().RequireRateLimiting("portal-login");
        group.MapPost("/logout", async (HttpContext context) => {
            await context.SignOutAsync(PortalServices.Scheme);
            context.User = new System.Security.Claims.ClaimsPrincipal();
            return TypedResults.NoContent();
        }).RequireAuthorization(policy => policy.AddAuthenticationSchemes(PortalServices.Scheme).RequireAuthenticatedUser());
        group.MapPost("/password", async (PortalPasswordRequest input, HttpContext context, PortalUserService users, CancellationToken ct) => {
            var user = await users.ChangePasswordAsync(context.User, input, ct);
            await PortalServices.SignInAsync(context, user);
            return TypedResults.Ok(PortalServices.Session(context, user));
        }).RequireAuthorization(policy => policy.AddAuthenticationSchemes(PortalServices.Scheme).RequireAuthenticatedUser()).RequireRateLimiting("portal-login");
        group.MapGet("/users", async (int? page, int? pageSize, PortalUserService users, CancellationToken ct) =>
            TypedResults.Ok(await users.ListAsync(page ?? 1, pageSize ?? 25, ct))).RequireAuthorization("portal-admin");
        group.MapPost("/users", async (PortalUserRequest input, PortalUserService users, CancellationToken ct) =>
            TypedResults.Ok(await users.CreateAsync(input, ct))).RequireAuthorization("portal-admin");
        group.MapPut("/users/{id:guid}", async (Guid id, PortalUserUpdate input, HttpContext context, PortalUserService users, CancellationToken ct) =>
            TypedResults.Ok(await users.UpdateAsync(context.User, id, input, ct))).RequireAuthorization("portal-admin");
        group.MapPost("/users/{id:guid}/reset-password", async (Guid id, PortalUserService users, CancellationToken ct) =>
            TypedResults.Ok(await users.ResetAsync(id, ct))).RequireAuthorization("portal-admin");
        return endpoints;
    }
}

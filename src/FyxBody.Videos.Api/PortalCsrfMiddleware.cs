using Microsoft.AspNetCore.Antiforgery;
namespace FyxBody.Videos.Api;

public sealed class PortalCsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.User.Identities.Any(identity => identity.IsAuthenticated && identity.AuthenticationType == "PortalCookie")
            && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException)
            {
                await ApiErrors.WriteAsync(context, 400, "invalid_csrf", "The security token is missing or expired; refresh your session.");
                return;
            }
        }
        await next(context);
    }
}

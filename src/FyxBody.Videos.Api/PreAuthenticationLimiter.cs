using System.Threading.RateLimiting;
namespace FyxBody.Videos.Api;

public sealed class PreAuthenticationLimiter : IAsyncDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> limiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    public ValueTask<RateLimitLease> AcquireAsync(HttpContext context) => limiter.AcquireAsync(context, 1, context.RequestAborted);
    public ValueTask DisposeAsync() => limiter.DisposeAsync();
}

public sealed class PreAuthenticationLimiterMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, PreAuthenticationLimiter limiter)
    {
        if (!context.Request.Path.StartsWithSegments("/v1") && !context.Request.Path.StartsWithSegments("/admin") && !context.Request.Path.StartsWithSegments("/portal/api")) { await next(context); return; }
        using var lease = await limiter.AcquireAsync(context);
        if (!lease.IsAcquired)
        {
            context.Response.Headers.RetryAfter = "60";
            await ApiErrors.WriteAsync(context, 429, "rate_limit_exceeded", "Too many requests; retry after the indicated delay.");
            return;
        }
        await next(context);
    }
}

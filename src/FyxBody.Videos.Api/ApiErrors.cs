using Microsoft.AspNetCore.Diagnostics;
using FyxBody.Videos.Api.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace FyxBody.Videos.Api;

public static class ApiErrors
{
    public static Task WriteAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        if (status == 429 && !context.Response.Headers.ContainsKey("Retry-After"))
        {
            var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            var nextMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
            var delay = code == "monthly_quota_exceeded" ? Math.Ceiling((nextMonth - now).TotalSeconds) : 60;
            context.Response.Headers.RetryAfter = Math.Max(1, delay).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        context.Response.Headers.CacheControl = "no-store";
        return Results.Problem(statusCode: status, title: ApiMessageCatalog.Translate(message, code),
            extensions: new Dictionary<string, object?> { ["code"] = code, ["requestId"] = context.TraceIdentifier }).ExecuteAsync(context);
    }
}

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, message) = exception switch
        {
            ApiException error => (error.Status, error.Code, error.Message),
            BadHttpRequestException => (400, "invalid_request", "The request could not be read."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => (409, "resource_conflict", "A resource with these identifiers already exists."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation } } => (422, "invalid_resource", "The resource violates a data constraint."),
            NpgsqlException or DbUpdateException => (503, "database_unavailable", "The database is temporarily unavailable."),
            _ => (500, "internal_error", "The request could not be completed.")
        };
        if (status >= 500) logger.LogError("API failure {ErrorType}; request {RequestId}", exception.GetType().Name, context.TraceIdentifier);
        await ApiErrors.WriteAsync(context, status, code, message);
        return true;
    }
}

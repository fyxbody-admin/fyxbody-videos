using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FyxBody.Videos.Api;

public sealed class VideoDatabaseHealthCheck(IServiceScopeFactory scopeFactory, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (VideoDatabaseConfiguration.GetConnectionString(configuration) is null)
                return HealthCheckResult.Unhealthy("Database configuration is missing.");
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<VideosDbContext>().Database;
            if (!await database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("Database is unavailable.");
            var pending = await database.GetPendingMigrationsAsync(cancellationToken);
            return pending.Any()
                ? HealthCheckResult.Unhealthy("Database migrations are pending.")
                : HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Database readiness check failed.");
        }
    }
}

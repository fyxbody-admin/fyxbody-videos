namespace FyxBody.Videos.Api.YouTube;

public sealed class YouTubeImportWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<YouTubeImportWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        configuration.GetValue<bool>("VideoImports:Enabled")
            ? Task.WhenAll(Enumerable.Range(0, YouTubeImportQueue.JobSlots(configuration)).Select(_ => ProcessQueue(stoppingToken)))
            : Task.CompletedTask;

    private async Task ProcessQueue(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (OperatingSystem.IsMacOS()
                    && configuration.GetValue<bool>("VideoImports:TakeoutOnly")
                    && configuration["VideoImports:LocalDrainFile"] is { Length: > 0 } drainFile
                    && File.Exists(drainFile))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                await using var scope = scopes.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<YouTubeImportQueue>();
                await queue.ExpireLeases(stoppingToken);
                var job = await queue.Claim(stoppingToken);
                if (job is null) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                else await scope.ServiceProvider.GetRequiredService<YouTubeImportRunner>().Run(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("YouTube import queue unavailable; error {ErrorType}.", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}

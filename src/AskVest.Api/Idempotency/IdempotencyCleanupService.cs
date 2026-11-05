using Microsoft.Extensions.Options;

namespace AskVest.Api;

internal sealed class IdempotencyCleanupService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<IdempotencyCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
                var purged = await store.PurgeExpiredAsync(timeProvider.GetUtcNow(), stoppingToken);
                if (purged > 0 && logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Purged {Count} expired idempotency records", purged);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is Microsoft.EntityFrameworkCore.DbUpdateException or Npgsql.NpgsqlException or TimeoutException)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(exception, "Idempotency cleanup failed");
                }
            }
        }
    }
}

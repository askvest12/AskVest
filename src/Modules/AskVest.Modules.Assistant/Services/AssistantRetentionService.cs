using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AskVest.Modules.Assistant.Services;

/// <summary>Hourly wrapper around <see cref="AssistantRetention"/>; a failed run logs and retries on the next tick.</summary>
public sealed class AssistantRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<AssistantRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var retention = scope.ServiceProvider.GetRequiredService<AssistantRetention>();
                await retention.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Assistant retention run failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

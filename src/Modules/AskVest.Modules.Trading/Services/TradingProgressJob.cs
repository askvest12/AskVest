using AskVest.Modules.Trading.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Trading.Services;

/// <summary>
/// Periodically polls non-final orders against the gateway. A session-level
/// advisory lock keeps exactly one instance working; each order is processed
/// in its own scope so one failure never stops the rest.
/// </summary>
internal sealed class TradingProgressJob(
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
    IOptions<TradingOptions> options,
    TimeProvider timeProvider,
    ILogger<TradingProgressJob> logger) : BackgroundService
{
    internal const long AdvisoryLockKey = 2026111210L;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunLockedCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Trading progress cycle failed unexpectedly.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.Value.ProgressIntervalSeconds),
                    timeProvider,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task<bool> RunCycleAsync(CancellationToken cancellationToken)
        => await RunLockedCycleAsync(cancellationToken);

    private async Task<bool> RunLockedCycleAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection))
        {
            lockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            var acquired = await lockCommand.ExecuteScalarAsync(cancellationToken);
            if (acquired is not true)
            {
                return false;
            }
        }

        try
        {
            await ProcessCandidatesAsync(cancellationToken);
            return true;
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            unlockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task ProcessCandidatesAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var stuckBefore = now - TimeSpan.FromSeconds(options.Value.SubmissionStuckSeconds);
        var orderIds = new List<Guid>();

        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand("""
            SELECT id FROM trading.orders
            WHERE state IN ('pending_submission', 'submitting', 'submitted', 'needs_reconciliation')
              AND (
                  (state IN ('pending_submission', 'submitting') AND updated_at < @stuckBefore)
                  OR state IN ('submitted', 'needs_reconciliation')
              )
            ORDER BY created_at, id
            LIMIT 200
            """, connection))
        {
            command.Parameters.AddWithValue("stuckBefore", stuckBefore);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                orderIds.Add(reader.GetGuid(0));
            }
        }

        foreach (var orderId in orderIds)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var progress = scope.ServiceProvider.GetRequiredService<OrderProgressService>();
                await progress.ProcessAsync(orderId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Trading progress failed for order {OrderId}.", orderId);
            }
        }
    }
}

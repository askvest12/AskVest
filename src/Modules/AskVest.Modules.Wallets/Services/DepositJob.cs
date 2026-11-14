using AskVest.Modules.Wallets.Configuration;
using AskVest.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Wallets.Services;

/// <summary>
/// Periodically discovers provider deposits. A session-level advisory lock
/// keeps exactly one instance working; each cycle runs in its own scope.
/// </summary>
internal sealed class DepositJob(
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
    IOptions<WalletsOptions> options,
    TimeProvider timeProvider,
    ILogger<DepositJob> logger) : BackgroundService
{
    internal const long AdvisoryLockKey = 2026111411L;

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
                logger.LogError(exception, "Deposit polling cycle failed unexpectedly.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.Value.DepositIntervalSeconds),
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
            await using var scope = scopeFactory.CreateAsyncScope();
            var wallets = scope.ServiceProvider.GetRequiredService<WalletService>();
            await wallets.ProcessDepositsAsync(cancellationToken);
            return true;
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            unlockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}

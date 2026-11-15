using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Persistence;
using AskVest.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// Deletes expired withdrawal previews and conversion quotes older than a
/// day. Committed withdrawals and completed conversions are financial
/// records and are never removed. A session-level advisory lock keeps
/// exactly one instance working.
/// </summary>
internal sealed class FundingCleanupJob(
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
    IOptions<FundingOptions> options,
    TimeProvider timeProvider,
    ILogger<FundingCleanupJob> logger) : BackgroundService
{
    internal const long AdvisoryLockKey = 2026111514L;

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
                logger.LogError(exception, "Funding cleanup cycle failed unexpectedly.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.Value.CleanupIntervalSeconds),
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
            var db = scope.ServiceProvider.GetRequiredService<FundingDbContext>();
            var now = timeProvider.GetUtcNow();
            var quoteCutoff = now - TimeSpan.FromHours(24);

            var expiredPreviews = await db.Withdrawals
                .Where(row => row.State == WithdrawalState.Previewed && row.ExpiresAt < now)
                .ExecuteDeleteAsync(cancellationToken);
            var oldQuotes = await db.Conversions
                .Where(row => row.State != ConversionState.Completed && row.CreatedAt < quoteCutoff)
                .ExecuteDeleteAsync(cancellationToken);
            if (expiredPreviews + oldQuotes > 0 && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Funding cleanup removed expired rows: previews and quotes.");
            }

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

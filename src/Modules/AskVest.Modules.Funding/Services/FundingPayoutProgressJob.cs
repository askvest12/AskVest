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
/// Periodically recovers stuck submissions, polls submitted and
/// reconciling withdrawals and runs the fiat reserves check on its own
/// interval. A session-level advisory lock keeps exactly one instance
/// working.
/// </summary>
internal sealed class FundingPayoutProgressJob(
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
    IOptions<FundingOptions> options,
    TimeProvider timeProvider,
    ILogger<FundingPayoutProgressJob> logger) : BackgroundService
{
    internal const long AdvisoryLockKey = 2026111513L;

    private DateTimeOffset _lastReservesRun = DateTimeOffset.MinValue;

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
                logger.LogError(exception, "Funding payout progress cycle failed unexpectedly.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.Value.PayoutPollIntervalSeconds),
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
            await RecoverStuckSubmissionsAsync(cancellationToken);
            await PollCandidatesAsync(cancellationToken);
            await MaybeRunReservesAsync(cancellationToken);
            return true;
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            unlockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task RecoverStuckSubmissionsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var stuckBefore = now - TimeSpan.FromSeconds(options.Value.SubmissionStuckSeconds);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FundingDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var stuck = await db.Withdrawals
            .Where(row => row.State == WithdrawalState.Submitting
                && row.UpdatedAt < stuckBefore)
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var row in stuck)
        {
            // A submitting row whose dispatcher died goes back to requested
            // and is re-enqueued; the provider call is idempotent on our id.
            row.State = WithdrawalState.Requested;
            row.UpdatedAt = now;
            await outbox.EnqueueAsync(
                FundingWithdrawalService.SubmitMessageType,
                new { withdrawalId = row.Id },
                $"funding:submit:{row.Id:N}",
                cancellationToken);
        }

        if (stuck.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task PollCandidatesAsync(CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand("""
            SELECT id FROM funding.withdrawals
            WHERE state IN ('submitted', 'needs_reconciliation')
            ORDER BY updated_at, id
            LIMIT 200
            """, connection))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        foreach (var id in ids)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var progress = scope.ServiceProvider.GetRequiredService<FundingPayoutProgressService>();
                await progress.ProcessAsync(id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Funding payout progress failed for one row.");
            }
        }
    }

    private async Task MaybeRunReservesAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMinutes(options.Value.ReservesIntervalMinutes);
        if (timeProvider.GetUtcNow() - _lastReservesRun < interval)
        {
            return;
        }

        _lastReservesRun = timeProvider.GetUtcNow();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var reserves = scope.ServiceProvider.GetRequiredService<FundingReservesService>();
            await reserves.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Funding reserves check failed unexpectedly.");
        }
    }
}

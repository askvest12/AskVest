using AskVest.Modules.Ledger.Configuration;
using AskVest.Modules.Ledger.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Ledger.Services;

internal sealed class LedgerReconciliationService(
    IServiceScopeFactory scopeFactory,
    IOptions<LedgerOptions> options,
    TimeProvider timeProvider,
    ILogger<LedgerReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Ledger reconciliation cycle failed.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromMinutes(options.Value.ReconciliationIntervalMinutes),
                    timeProvider,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var pageSize = options.Value.ReconciliationPageSize;

        var mismatches = 0;
        var offset = 0;
        while (true)
        {
            var page = await db.AccountBalances
                .OrderBy(balance => balance.AccountId)
                .Skip(offset)
                .Take(pageSize)
                .Select(balance => new { balance.AccountId, balance.Balance })
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            var ids = page.Select(row => row.AccountId).ToList();
            var sums = await db.JournalEntries
                .Where(entry => ids.Contains(entry.AccountId))
                .GroupBy(entry => entry.AccountId)
                .Select(group => new { AccountId = group.Key, Sum = group.Sum(entry => entry.Amount) })
                .ToDictionaryAsync(row => row.AccountId, row => row.Sum, cancellationToken);

            foreach (var row in page)
            {
                var entrySum = sums.GetValueOrDefault(row.AccountId);
                var delta = row.Balance - entrySum;
                if (delta != 0m)
                {
                    mismatches++;
                    var target = row.AccountId;
                    logger.LogError(
                        "Ledger reconciliation mismatch for {Target} with delta {Delta}",
                        target,
                        delta);
                }
            }

            offset += page.Count;
            if (page.Count < pageSize)
            {
                break;
            }
        }

        var nonZeroNets = await db.AccountBalances
            .Join(
                db.Accounts,
                balance => balance.AccountId,
                account => account.Id,
                (balance, account) => new { account.Book, account.AssetCode, balance.Balance })
            .GroupBy(row => new { row.Book, row.AssetCode })
            .Select(group => new { group.Key.Book, group.Key.AssetCode, Net = group.Sum(row => row.Balance) })
            .Where(row => row.Net != 0m)
            .ToListAsync(cancellationToken);
        foreach (var row in nonZeroNets)
        {
            mismatches++;
            logger.LogError(
                "Ledger reconciliation non-zero net for book {Book} asset {Asset} with net {Net}",
                LedgerText.ToDb(row.Book),
                row.AssetCode,
                row.Net);
        }

        if (mismatches == 0)
        {
            logger.LogInformation("Ledger reconciliation completed with zero mismatches.");
        }
        else
        {
            logger.LogError("Ledger reconciliation completed with {Count} mismatches.", mismatches);
        }
    }
}

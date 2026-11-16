using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Wallets.Configuration;
using AskVest.Modules.Wallets.Custody;
using AskVest.Modules.Wallets.Domain;
using AskVest.Modules.Wallets.Persistence;
using AskVest.Modules.Wallets.Persistence.Entities;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets.Services;

/// <summary>
/// The reserves check: custody holdings must equal the ledger totals custody
/// is responsible for, per chain and token. In-flight withdrawals are
/// excluded on both sides so they cannot cause false pauses. Two consecutive
/// mismatches pause the scope; the pause persists until an operator resumes.
/// </summary>
internal sealed class ReservesService(
    WalletsDbContext db,
    ICatalog catalog,
    CustodyRegistry custody,
    ILedger ledger,
    ApprovedStablecoins approvedStablecoins,
    IOptions<WalletsOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    ILogger<ReservesService> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var holdings = new Dictionary<(string ChainId, string Contract), decimal>(ValueTupleComparer.Instance);
        foreach (var (chainId, _) in options.Value.Custody.RouteProviders)
        {
            if (!custody.TryResolve(chainId, out var provider))
            {
                continue;
            }

            var result = await provider.GetHoldingsAsync(cancellationToken);
            if (result.IsFailure)
            {
                logger.LogWarning("Custody holdings unavailable for a routed chain.");
                continue;
            }

            foreach (var holding in result.Value)
            {
                holdings[(holding.ChainId, holding.TokenContractAddress)] = holding.Quantity;
            }
        }

        var totals = await ledger.GetAssetTotalsAsync(Book.Real, cancellationToken);
        if (totals.IsFailure)
        {
            logger.LogWarning("Ledger asset totals unavailable for reserves check.");
            return;
        }

        // Map ledger asset codes back to chain contracts through the catalog.
        var ledgerByContract = new Dictionary<(string ChainId, string Contract), decimal>(ValueTupleComparer.Instance);
        foreach (var total in totals.Value)
        {
            var token = await catalog.FindTokenByAssetCodeAsync(total.AssetCode, cancellationToken);
            if (token is null)
            {
                continue;
            }

            var key = (token.ChainId, token.CanonicalAddress);
            ledgerByContract[key] = ledgerByContract.GetValueOrDefault(key) + total.Total;
        }

        // Acknowledged submissions may already be paid out on the provider
        // side while our ledger still holds the gross; model the payout so
        // the in-flight window cannot cause false pauses. Needs-reconciliation
        // rows are deliberately not offset: a payout we cannot confirm is a
        // real discrepancy an operator must see.
        var inFlight = await db.Withdrawals.AsNoTracking()
            .Where(row => row.State == WithdrawalState.Submitted)
            .ToListAsync(cancellationToken);
        foreach (var group in inFlight.GroupBy(row => row.TokenId))
        {
            var token = await catalog.FindTokenAsync(group.Key, cancellationToken);
            if (token is null)
            {
                continue;
            }

            var key = (token.ChainId, token.CanonicalAddress);
            if (ledgerByContract.TryGetValue(key, out var current))
            {
                ledgerByContract[key] = current - group.Sum(row => row.NetQuantity);
            }
        }

        var scopes = holdings.Keys.Union(ledgerByContract.Keys).ToList();
        foreach (var scopeKey in scopes)
        {
            var custodyQuantity = holdings.GetValueOrDefault(scopeKey);
            var ledgerQuantity = ledgerByContract.GetValueOrDefault(scopeKey);
            if (ReservesComparison.Matches(custodyQuantity, ledgerQuantity))
            {
                await RecordMatchAsync(scopeKey, cancellationToken);
                continue;
            }

            await RecordMismatchAsync(scopeKey, custodyQuantity, ledgerQuantity, cancellationToken);
        }

        await CheckStablecoinsAsync(holdings, totals.Value, cancellationToken);
    }

    /// <summary>
    /// Approved stablecoins credit cash assets, so their reserves compare per
    /// asset code with holdings aggregated across every approved chain.
    /// Pending deposits on a covered chain skip that asset for the run,
    /// mirroring the in-flight rule for tokens.
    /// </summary>
    private async Task CheckStablecoinsAsync(
        IReadOnlyDictionary<(string ChainId, string Contract), decimal> holdings,
        IReadOnlyList<LedgerAssetTotal> totals,
        CancellationToken cancellationToken)
    {
        var pendingChains = await db.Deposits.AsNoTracking()
            .Where(row => row.State == DepositState.Pending)
            .Select(row => row.ChainId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var group in approvedStablecoins.Entries.GroupBy(entry => entry.LedgerAssetCode))
        {
            if (group.Any(entry => pendingChains.Contains(entry.ChainId)))
            {
                continue;
            }

            var custodyTotal = group.Sum(entry =>
                holdings.GetValueOrDefault((entry.ChainId, entry.Contract)));
            var ledgerTotal = totals
                .Where(total => string.Equals(total.AssetCode, group.Key, StringComparison.Ordinal))
                .Sum(total => total.Total);
            var matches = ReservesComparison.Matches(custodyTotal, ledgerTotal);
            var (_, activated) = await UpsertPauseAsync(group.Key, null, matches, cancellationToken);
            if (activated)
            {
                logger.LogWarning(
                    "Custody pause activated for a stablecoin reserves mismatch on asset {AssetCode}.",
                    group.Key);
            }
        }
    }

    private async Task RecordMatchAsync(
        (string ChainId, string Contract) scopeKey,
        CancellationToken cancellationToken)
    {
        var token = await TokenForAsync(scopeKey, cancellationToken);
        await UpsertPauseAsync(scopeKey.ChainId, token?.Id, matches: true, cancellationToken);
    }

    private async Task RecordMismatchAsync(
        (string ChainId, string Contract) scopeKey,
        decimal custodyQuantity,
        decimal ledgerQuantity,
        CancellationToken cancellationToken)
    {
        var token = await TokenForAsync(scopeKey, cancellationToken);
        var (_, activated) = await UpsertPauseAsync(
            scopeKey.ChainId, token?.Id, matches: false, cancellationToken);
        if (activated)
        {
            logger.LogWarning(
                "Custody pause activated for a reserves mismatch on chain {ChainId}.",
                scopeKey.ChainId);
        }
    }

    private async Task<(CustodyPause? Pause, bool Activated)> UpsertPauseAsync(
        string chainId,
        Guid? tokenId,
        bool matches,
        CancellationToken cancellationToken)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            if (scope.Transaction is not null)
            {
                await db.Database.UseTransactionAsync(scope.Transaction, cancellationToken);
            }

            var pause = await db.CustodyPauses
                .FirstOrDefaultAsync(
                    row => row.ChainId == chainId && row.TokenId == tokenId,
                    cancellationToken);
            if (pause is null)
            {
                pause = new CustodyPause
                {
                    Id = Guid.CreateVersion7(),
                    ChainId = chainId,
                    TokenId = tokenId,
                    ReasonCode = string.Empty,
                    CreatedAt = timeProvider.GetUtcNow(),
                    UpdatedAt = timeProvider.GetUtcNow(),
                };
                db.CustodyPauses.Add(pause);
            }

            var now = timeProvider.GetUtcNow();
            pause.ConsecutiveMismatches = ReservesComparison.NextMismatchCount(
                pause.ConsecutiveMismatches, matches);
            var activated = !matches
                && pause.PausedAt is null
                && ReservesComparison.ShouldPause(
                    pause.ConsecutiveMismatches, options.Value.ReservesMismatchThreshold);
            if (activated)
            {
                pause.PausedAt = now;
                pause.ReasonCode = "reserves_mismatch";
            }

            pause.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            return (pause, activated);
        }
        catch
        {
            if (ownsScope && scope.IsActive)
            {
                await scope.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    private async Task<CatalogToken?> TokenForAsync(
        (string ChainId, string Contract) scopeKey,
        CancellationToken cancellationToken)
    {
        var tokens = await catalog.ListListedTokensForChainAsync(scopeKey.ChainId, cancellationToken);
        return tokens.FirstOrDefault(token =>
            string.Equals(token.CanonicalAddress, scopeKey.Contract, StringComparison.Ordinal));
    }

    private sealed class ValueTupleComparer : IEqualityComparer<(string ChainId, string Contract)>
    {
        public static readonly ValueTupleComparer Instance = new();

        public bool Equals((string ChainId, string Contract) x, (string ChainId, string Contract) y)
            => string.Equals(x.ChainId, y.ChainId, StringComparison.Ordinal)
                && string.Equals(x.Contract, y.Contract, StringComparison.Ordinal);

        public int GetHashCode((string ChainId, string Contract) obj)
            => HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.ChainId),
                StringComparer.Ordinal.GetHashCode(obj.Contract));
    }
}

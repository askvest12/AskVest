using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Wallets.Custody;
using AskVest.Modules.Wallets.Domain;
using AskVest.Modules.Wallets.Persistence;
using AskVest.Modules.Wallets.Persistence.Entities;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AskVest.Modules.Wallets.Configuration;

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
        var threshold = options.Value.ReservesMismatchThreshold;
        var pause = await UpsertPauseAsync(scopeKey.ChainId, token?.Id, matches: false, cancellationToken);
        if (pause is not null
            && ReservesComparison.ShouldPause(pause.ConsecutiveMismatches, threshold)
            && pause.PausedAt is null)
        {
            pause.PausedAt = timeProvider.GetUtcNow();
            pause.ReasonCode = "reserves_mismatch";
            pause.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "Custody pause activated for a reserves mismatch on chain {ChainId}.",
                scopeKey.ChainId);
        }
    }

    private async Task<CustodyPause?> UpsertPauseAsync(
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

            pause.ConsecutiveMismatches = ReservesComparison.NextMismatchCount(
                pause.ConsecutiveMismatches, matches);
            pause.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            return pause;
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

using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.MarketData.Contracts;
using AskVest.Modules.Portfolio.Configuration;
using AskVest.Modules.Portfolio.Contracts;
using AskVest.Modules.Portfolio.Domain;
using AskVest.Modules.Portfolio.Persistence;
using AskVest.Modules.Practice.Contracts;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Portfolio.Services;

/// <summary>
/// Computes a user's portfolio on demand by replaying ledger movements. It
/// stores no money facts; the ledger remains the only source of truth.
/// </summary>
internal sealed class PortfolioService(
    ILedger ledger,
    ICatalog catalog,
    IMarketData marketData,
    IPractice practice,
    PortfolioDbContext db,
    IOptions<PortfolioOptions> options,
    TimeProvider timeProvider,
    ILogger<PortfolioService> logger) : IPortfolio
{
    private const int MovementPageSize = 500;

    public async Task<Result<PortfolioResponse>> GetPortfolioAsync(
        Guid userId,
        Book book,
        CancellationToken cancellationToken = default)
    {
        var movements = await LoadMovementsAsync(userId, book, cancellationToken);
        var epochMovements = AfterLatestReset(movements);

        var assetCodes = epochMovements.Select(movement => movement.AssetCode).Distinct(StringComparer.Ordinal).ToList();
        var kinds = await LoadAssetKindsAsync(assetCodes, cancellationToken);
        var resolutions = await ResolveAssetsAsync(book, assetCodes, cancellationToken);
        var balances = (await ledger.GetBalancesAsync(userId, book, cancellationToken)).Value!;

        var replay = PortfolioReplay.Run(
            epochMovements,
            kinds,
            assetCode => resolutions.GetValueOrDefault(assetCode)?.InstrumentId,
            options.Value.MaxJournalsPerReplay);
        if (replay.IsFailure)
        {
            return Result.Failure<PortfolioResponse>(replay.Error!);
        }

        var result = replay.Value!;
        var hasInconsistency = result.HasInconsistency
            || CheckConsistencyAsync(userId, book, result, balances, kinds);

        return Result.Success(await BuildResponseAsync(
            book, result, resolutions, balances, kinds, hasInconsistency, cancellationToken));
    }

    public async Task<IReadOnlyList<WatchlistEntry>> GetWatchlistAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await db.WatchlistItems.AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderBy(item => item.AddedAt)
            .ThenBy(item => item.InstrumentId)
            .Select(item => new WatchlistEntry(item.InstrumentId, item.AddedAt))
            .ToListAsync(cancellationToken);
    }

    private async Task<List<LedgerMovement>> LoadMovementsAsync(
        Guid userId,
        Book book,
        CancellationToken cancellationToken)
    {
        var movements = new List<LedgerMovement>();
        string? cursor = null;
        PageResult<LedgerMovement> page;
        do
        {
            page = await ledger.GetMovementsAsync(userId, book, MovementPageSize, cursor, cancellationToken);
            movements.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (page.HasMore);

        return movements;
    }

    private static List<LedgerMovement> AfterLatestReset(List<LedgerMovement> movements)
    {
        var lastReset = -1;
        for (var index = 0; index < movements.Count; index++)
        {
            if (movements[index].ReasonCode == LedgerReasonCodes.PracticeReset)
            {
                lastReset = index;
            }
        }

        if (lastReset < 0)
        {
            return movements.ToList();
        }

        // Rows of one journal are contiguous in posted order, so the last
        // reset row is also the last row of the latest reset journal.
        return movements.Skip(lastReset + 1).ToList();
    }

    private async Task<Dictionary<string, AssetKind>> LoadAssetKindsAsync(
        IReadOnlyList<string> assetCodes,
        CancellationToken cancellationToken)
    {
        var kinds = new Dictionary<string, AssetKind>(StringComparer.Ordinal);
        foreach (var code in assetCodes)
        {
            var kind = await ledger.GetAssetKindAsync(code, cancellationToken);
            if (kind is { } value)
            {
                kinds[code] = value;
            }
        }

        return kinds;
    }

    private async Task<Dictionary<string, AssetResolution>> ResolveAssetsAsync(
        Book book,
        IReadOnlyList<string> assetCodes,
        CancellationToken cancellationToken)
    {
        var resolutions = new Dictionary<string, AssetResolution>(StringComparer.Ordinal);
        var tokenAssets = assetCodes.Where(code => !code.StartsWith("P.", StringComparison.Ordinal)).ToList();
        var practiceAssets = assetCodes.Where(code => code.StartsWith("P.", StringComparison.Ordinal)).ToList();

        if (book == Book.Real)
        {
            foreach (var code in tokenAssets)
            {
                var token = await catalog.FindTokenByAssetCodeAsync(code, cancellationToken);
                if (token is not null)
                {
                    resolutions[code] = new AssetResolution(token.InstrumentId, token.ChainId, token.Symbol);
                }
            }
        }
        else
        {
            var map = await practice.GetInstrumentIdsForAssetCodesAsync(practiceAssets, cancellationToken);
            foreach (var (code, instrumentId) in map)
            {
                resolutions[code] = new AssetResolution(instrumentId, null, null);
            }
        }

        return resolutions;
    }

    private bool CheckConsistencyAsync(
        Guid userId,
        Book book,
        PortfolioReplayResult result,
        IReadOnlyList<LedgerBalance> balances,
        Dictionary<string, AssetKind> kinds)
    {
        var replayByAsset = result.Assets.ToDictionary(asset => asset.AssetCode, StringComparer.Ordinal);
        var balanceByAsset = balances
            .Where(balance => IsPositionAsset(balance.AssetCode, kinds))
            .ToDictionary(balance => balance.AssetCode, balance => balance.Available + balance.Held, StringComparer.Ordinal);

        var mismatched = balanceByAsset
            .Where(pair => replayByAsset.GetValueOrDefault(pair.Key)?.Quantity != pair.Value)
            .Select(pair => pair.Key)
            .Concat(replayByAsset
                .Where(pair => IsPositionAsset(pair.Key, kinds) && !balanceByAsset.ContainsKey(pair.Key) && pair.Value.Quantity != 0m)
                .Select(pair => pair.Key))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var assetCode in mismatched)
        {
            logger.LogError(
                "Portfolio replay mismatch for user {UserId} book {Book} asset {AssetCode}.",
                userId, book, assetCode);
        }

        return mismatched.Count > 0;
    }

    private static bool IsPositionAsset(string assetCode, Dictionary<string, AssetKind> kinds)
        => !kinds.TryGetValue(assetCode, out var kind) || kind == AssetKind.Token;

    private async Task<PortfolioResponse> BuildResponseAsync(
        Book book,
        PortfolioReplayResult result,
        IReadOnlyDictionary<string, AssetResolution> resolutions,
        IReadOnlyList<LedgerBalance> balances,
        Dictionary<string, AssetKind> kinds,
        bool hasInconsistency,
        CancellationToken cancellationToken)
    {
        var cashEquivalent = options.Value.CashEquivalentAssets
            .Select(asset => asset.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var decimals = options.Value.DisplayDecimals;

        var cashLines = balances
            .Where(balance => kinds.TryGetValue(balance.AssetCode, out var kind)
                && kind is AssetKind.Fiat or AssetKind.Stablecoin)
            .OrderBy(balance => balance.AssetCode, StringComparer.Ordinal)
            .Select(balance => new PortfolioCashLine(
                balance.AssetCode,
                kinds[balance.AssetCode].ToString().ToLowerInvariant(),
                PortfolioRounding.RoundDisplay(balance.Available, decimals),
                PortfolioRounding.RoundDisplay(balance.Held, decimals),
                cashEquivalent.Contains(balance.AssetCode)))
            .ToList();

        var grouped = result.Assets
            .Where(asset => asset.Quantity > 0m && asset.InstrumentId is not null)
            .GroupBy(asset => asset.InstrumentId!)
            .ToList();
        var instrumentIds = grouped.Select(group => group.Key).ToList();
        var instruments = new Dictionary<string, CatalogInstrument>(StringComparer.Ordinal);
        foreach (var id in instrumentIds)
        {
            var instrument = await catalog.FindInstrumentAsync(id, cancellationToken);
            if (instrument is not null)
            {
                instruments[id] = instrument;
            }
        }

        var prices = (await marketData.GetLatestPricesAsync(instrumentIds, cancellationToken))
            .ToDictionary(price => price.InstrumentId, StringComparer.Ordinal);
        var balanceByAsset = balances.ToDictionary(
            balance => balance.AssetCode,
            balance => (balance.Available, balance.Held),
            StringComparer.Ordinal);

        var holdings = new List<PortfolioHolding>();
        decimal holdingsValue = 0m;
        decimal totalCostBasis = 0m;
        decimal totalUnrealized = 0m;
        var unvaluedCount = 0;

        foreach (var group in grouped)
        {
            var instrumentId = group.Key;
            var assets = group.ToList();
            var quantity = assets.Sum(asset => asset.Quantity);
            var costBasis = assets.Sum(asset => asset.TotalCost);
            var realized = assets.Sum(asset => asset.RealizedProfit);
            var income = result.IncomeByInstrument.GetValueOrDefault(instrumentId);
            var flags = new List<string>();
            if (assets.Any(asset => asset.HasUnclassifiedMovements))
            {
                flags.Add("has_unclassified_movements");
            }
            if (hasInconsistency)
            {
                flags.Add("has_inconsistency");
            }

            var instrument = instruments.GetValueOrDefault(instrumentId);
            decimal? marketValue = null;
            decimal? unrealized = null;
            decimal? unrealizedPercent = null;
            decimal? averageCost = quantity > 0m ? costBasis / quantity : null;
            var price = prices.GetValueOrDefault(instrumentId);

            if (instrument is null || !string.Equals(instrument.QuoteCurrency, "USD", StringComparison.Ordinal))
            {
                flags.Add("valuation_unsupported_currency");
            }
            else if (price?.Price is null || !string.Equals(price.Currency, "USD", StringComparison.Ordinal))
            {
                flags.Add("price_unavailable");
            }
            else
            {
                marketValue = quantity * price.Price.Value;
                unrealized = marketValue.Value - costBasis;
                unrealizedPercent = costBasis > 0m ? unrealized.Value / costBasis * 100m : null;
                holdingsValue += marketValue.Value;
                totalUnrealized += unrealized.Value;
            }

            if (marketValue is null)
            {
                unvaluedCount++;
            }

            totalCostBasis += costBasis;

            var tokens = assets
                .OrderBy(asset => asset.AssetCode, StringComparer.Ordinal)
                .Select(asset =>
                {
                    var resolution = resolutions.GetValueOrDefault(asset.AssetCode);
                    var (available, held) = balanceByAsset.GetValueOrDefault(asset.AssetCode);
                    return new PortfolioTokenBreakdown(
                        asset.AssetCode,
                        resolution?.ChainId,
                        resolution?.Symbol,
                        available,
                        held,
                        PortfolioRounding.RoundDisplay(asset.TotalCost, decimals));
                })
                .ToList();

            holdings.Add(new PortfolioHolding(
                instrumentId,
                instrument?.Symbol ?? instrumentId,
                instrument?.Name ?? instrumentId,
                quantity,
                PortfolioRounding.RoundDisplay(costBasis, decimals),
                averageCost,
                price?.Price,
                price?.Currency,
                price?.Label,
                price?.Session,
                price?.UsableForExecution ?? false,
                marketValue is { } mv ? PortfolioRounding.RoundDisplay(mv, decimals) : null,
                unrealized is { } up ? PortfolioRounding.RoundDisplay(up, decimals) : null,
                unrealizedPercent is { } upp ? PortfolioRounding.RoundDisplay(upp, decimals) : null,
                PortfolioRounding.RoundDisplay(realized, decimals),
                PortfolioRounding.RoundDisplay(income, decimals),
                flags,
                tokens));
        }

        var cashTotal = cashLines.Where(line => line.IncludedInTotal).Sum(line => line.Available + line.Held);
        var totalIncome = result.UnattributedIncome + result.IncomeByInstrument.Values.Sum();
        var totalRealized = result.Assets.Sum(asset => asset.RealizedProfit);
        var partial = unvaluedCount > 0;

        return new PortfolioResponse(
            book,
            timeProvider.GetUtcNow(),
            "USD",
            partial,
            cashLines,
            holdings
                .OrderBy(holding => holding.Symbol, StringComparer.Ordinal)
                .ToList(),
            new PortfolioTotals(
                PortfolioRounding.RoundDisplay(cashTotal, decimals),
                PortfolioRounding.RoundDisplay(holdingsValue, decimals),
                PortfolioRounding.RoundDisplay(totalCostBasis, decimals),
                PortfolioRounding.RoundDisplay(totalUnrealized, decimals),
                PortfolioRounding.RoundDisplay(totalRealized, decimals),
                PortfolioRounding.RoundDisplay(totalIncome, decimals),
                PortfolioRounding.RoundDisplay(cashTotal + holdingsValue, decimals)));
    }

    private sealed record AssetResolution(string InstrumentId, string? ChainId, string? Symbol);
}

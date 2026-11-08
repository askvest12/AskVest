using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.MarketData.Contracts;
using AskVest.Modules.Practice.Persistence;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Practice.Services;

/// <summary>
/// Positions are derived, never stored: ledger practice balances mapped back to
/// instruments through practice_instruments, priced by the market data contract.
/// </summary>
internal sealed class PracticePositionService(
    PracticeDbContext db,
    ILedger ledger,
    IMarketData marketData,
    ICatalog catalog)
{
    private const string CashAsset = "USD";

    public async Task<Result<PracticePositions>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var balances = await ledger.GetBalancesAsync(userId, Book.Practice, cancellationToken);
        if (balances.IsFailure)
        {
            return Result.Failure<PracticePositions>(balances.Error!);
        }

        var cash = balances.Value
            .FirstOrDefault(balance => balance.AssetCode == CashAsset)?.Available ?? 0m;
        var assetBalances = balances.Value
            .Where(balance => balance.AssetCode != CashAsset && (balance.Available != 0m || balance.Held != 0m))
            .ToList();
        if (assetBalances.Count == 0)
        {
            return Result.Success(new PracticePositions(cash, []));
        }

        var assetCodes = assetBalances.Select(balance => balance.AssetCode).ToList();
        var mappings = await db.PracticeInstruments.AsNoTracking()
            .Where(row => assetCodes.Contains(row.AssetCode))
            .ToDictionaryAsync(row => row.AssetCode, row => row.InstrumentId, StringComparer.Ordinal, cancellationToken);

        var items = new List<PracticePositionItem>();
        var priced = new List<(PracticePositionItem Item, string InstrumentId)>();
        foreach (var balance in assetBalances)
        {
            if (!mappings.TryGetValue(balance.AssetCode, out var instrumentId))
            {
                continue;
            }

            var instrument = await catalog.FindInstrumentAsync(instrumentId, cancellationToken);
            priced.Add((new PracticePositionItem(
                instrumentId,
                instrument?.Symbol ?? instrumentId,
                instrument?.Name ?? instrumentId,
                balance.Available,
                balance.Held,
                null,
                null,
                null), instrumentId));
        }

        if (priced.Count == 0)
        {
            return Result.Success(new PracticePositions(cash, []));
        }

        var prices = await marketData.GetLatestPricesAsync(
            priced.Select(entry => entry.InstrumentId).ToList(), cancellationToken);
        var priceByInstrument = prices.ToDictionary(price => price.InstrumentId, StringComparer.Ordinal);
        foreach (var (item, instrumentId) in priced)
        {
            var price = priceByInstrument.GetValueOrDefault(instrumentId);
            var marketValue = price?.Price is { } latest
                ? PracticeRounding.RoundDisplay((item.AvailableQuantity + item.HeldQuantity) * latest)
                : (decimal?)null;
            items.Add(item with
            {
                Price = price?.Price,
                PriceLabel = price?.Label,
                MarketValue = marketValue,
            });
        }

        return Result.Success(new PracticePositions(cash, items));
    }
}

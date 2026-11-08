namespace AskVest.Modules.MarketData.Contracts;

public interface IMarketData
{
    Task<MarketDataPrice> GetLatestPriceAsync(string instrumentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MarketDataPrice>> GetLatestPricesAsync(
        IReadOnlyList<string> instrumentIds,
        CancellationToken cancellationToken = default);
}

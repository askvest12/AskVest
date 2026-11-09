using AskVest.SharedKernel;

namespace AskVest.Modules.MarketData.Contracts;

public interface IMarketData
{
    Task<MarketDataPrice> GetLatestPriceAsync(string instrumentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MarketDataPrice>> GetLatestPricesAsync(
        IReadOnlyList<string> instrumentIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cached or provider-filled candles for one instrument, interval and UTC
    /// range. Fails with marketdata.unavailable when nothing is cached and the
    /// provider fails; serves cached rows marked stale otherwise.
    /// </summary>
    Task<Result<MarketDataCandleSet>> GetCandlesAsync(
        string instrumentId,
        string interval,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);
}

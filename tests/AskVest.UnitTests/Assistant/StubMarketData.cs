using AskVest.Modules.MarketData.Contracts;
using AskVest.SharedKernel;

namespace AskVest.UnitTests.Assistant;

internal sealed class StubMarketData : IMarketData
{
    public List<MarketDataPrice> Prices { get; } = [];

    public MarketDataCandleSet? CandleSet { get; set; }

    public Task<MarketDataPrice> GetLatestPriceAsync(string instrumentId, CancellationToken cancellationToken = default)
        => Task.FromResult(Prices.FirstOrDefault(
            price => price.InstrumentId == instrumentId)
            ?? new MarketDataPrice(instrumentId, null, null, null, 0, "closed", "unavailable", false, "none", null));

    public Task<IReadOnlyList<MarketDataPrice>> GetLatestPricesAsync(
        IReadOnlyList<string> instrumentIds, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MarketDataPrice>>(
            Prices.Where(price => instrumentIds.Contains(price.InstrumentId, StringComparer.Ordinal)).ToList());

    public Task<Result<MarketDataCandleSet>> GetCandlesAsync(
        string instrumentId, string interval, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CandleSet is null
            ? Result.Failure<MarketDataCandleSet>(new Error("marketdata.unavailable", "No candles.", ErrorType.Unavailable))
            : Result.Success(CandleSet));
}

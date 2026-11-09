namespace AskVest.Modules.MarketData.Contracts;

/// <summary>
/// A candle series for one instrument and range. <see cref="Stale"/> is true
/// when the provider failed and only cached rows were served.
/// </summary>
public sealed record MarketDataCandleSet(bool Stale, IReadOnlyList<MarketDataCandle> Candles);

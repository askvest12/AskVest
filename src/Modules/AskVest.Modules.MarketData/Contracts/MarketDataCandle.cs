namespace AskVest.Modules.MarketData.Contracts;

/// <summary>
/// One OHLCV candle for cross-module consumers. All values are exact
/// decimals; the provider's adjusted flag is preserved.
/// </summary>
public sealed record MarketDataCandle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    bool Adjusted,
    bool Partial);

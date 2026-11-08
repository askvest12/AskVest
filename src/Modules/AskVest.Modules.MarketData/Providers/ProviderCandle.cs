namespace AskVest.Modules.MarketData.Providers;

internal sealed record ProviderCandle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    bool Adjusted);

namespace AskVest.Modules.MarketData.Contracts;

public sealed record MarketDataPrice(
    string InstrumentId,
    decimal? Price,
    string? Currency,
    DateTimeOffset? AsOf,
    long AgeSeconds,
    string Session,
    string Label,
    bool UsableForExecution,
    string Source,
    decimal? PreviousClose);

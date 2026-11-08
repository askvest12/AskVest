namespace AskVest.Modules.Portfolio.Contracts;

/// <summary>
/// One watchlist row with the instrument's catalog fields and its latest
/// market data label. A delisted or unpriced instrument still appears with
/// label <c>unavailable</c>.
/// </summary>
public sealed record WatchlistEntry(
    string InstrumentId,
    string Symbol,
    string Name,
    string Status,
    decimal? Price,
    string? Currency,
    string Label,
    string? Session,
    bool UsableForExecution,
    DateTimeOffset AddedAt);

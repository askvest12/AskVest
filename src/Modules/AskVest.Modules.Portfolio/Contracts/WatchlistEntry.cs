namespace AskVest.Modules.Portfolio.Contracts;

public sealed record WatchlistEntry(
    string InstrumentId,
    DateTimeOffset AddedAt);

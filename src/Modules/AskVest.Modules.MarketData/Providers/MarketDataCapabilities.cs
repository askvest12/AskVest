namespace AskVest.Modules.MarketData.Providers;

internal sealed record MarketDataCapabilities(
    int DelayMinutes,
    int MaxBatchSize,
    IReadOnlySet<string> SupportedIntervals,
    bool ReportsSession);

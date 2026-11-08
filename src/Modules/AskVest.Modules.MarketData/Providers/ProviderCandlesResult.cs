namespace AskVest.Modules.MarketData.Providers;

internal sealed record ProviderCandlesResult(
    ProviderOutcome Outcome,
    TimeSpan? RetryAfter,
    IReadOnlyList<ProviderCandle> Candles);

namespace AskVest.Modules.MarketData.Providers;

internal sealed record ProviderQuotesResult(
    ProviderOutcome Outcome,
    TimeSpan? RetryAfter,
    IReadOnlyList<ProviderQuote> Quotes,
    IReadOnlyList<string> UnknownSymbols);

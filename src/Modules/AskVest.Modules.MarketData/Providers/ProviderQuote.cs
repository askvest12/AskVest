namespace AskVest.Modules.MarketData.Providers;

internal sealed record ProviderQuote(
    string ProviderSymbol,
    decimal Price,
    string Currency,
    DateTimeOffset AsOf,
    string Session,
    decimal? PreviousClose);

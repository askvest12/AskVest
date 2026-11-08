namespace AskVest.Modules.MarketData.Providers;

internal interface IMarketDataProvider
{
    string ProviderKey { get; }

    MarketDataCapabilities Capabilities { get; }

    Task<ProviderQuotesResult> GetLatestQuotesAsync(
        IReadOnlyList<string> providerSymbols,
        CancellationToken cancellationToken);

    Task<ProviderCandlesResult> GetCandlesAsync(
        string providerSymbol,
        string interval,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}

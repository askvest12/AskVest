namespace AskVest.Modules.MarketData.Providers;

internal enum ProviderOutcome
{
    Succeeded,
    TransientFailure,
    RateLimited,
    AuthenticationFailure,
    SymbolNotFound,
}

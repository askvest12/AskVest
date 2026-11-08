using AskVest.Modules.MarketData.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.MarketData.Providers;

internal static class ProviderRegistry
{
    public const string FakeKey = "fake";

    public static bool IsSupported(string? providerKey)
        => string.Equals(providerKey, FakeKey, StringComparison.OrdinalIgnoreCase);

    public static IMarketDataProvider Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<MarketDataOptions>>().Value;
        return options.Provider.ToLowerInvariant() switch
        {
            FakeKey => services.GetRequiredService<FakeMarketDataProvider>(),
            _ => throw new InvalidOperationException($"Market data provider '{options.Provider}' is not supported."),
        };
    }
}

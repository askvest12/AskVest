using AskVest.Modules.Trading.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Trading.Gateway;

/// <summary>
/// Resolves the gateway for a catalog gateway key through the configured
/// routes. Only the fake provider exists in this phase; a real adapter adds
/// its provider name here when research picks a venue.
/// </summary>
internal sealed class GatewayRegistry(IOptions<TradingOptions> options, FakeIssuerGateway fake)
{
    public bool TryResolve(string gatewayKey, out IIssuerGateway gateway)
    {
        if (options.Value.Gateway.Routes.TryGetValue(gatewayKey, out var route)
            && string.Equals(route.Provider, FakeIssuerGateway.ProviderName, StringComparison.Ordinal))
        {
            gateway = fake;
            return true;
        }

        gateway = null!;
        return false;
    }
}

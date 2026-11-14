using AskVest.Modules.Wallets.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets.Custody;

/// <summary>
/// Resolves the custody provider for a chain through the configured routes.
/// Only the fake provider exists in this phase.
/// </summary>
internal sealed class CustodyRegistry(IOptions<WalletsOptions> options, FakeCustodyProvider fake)
{
    public bool TryResolve(string chainId, out ICustodyProvider provider)
    {
        if (options.Value.Custody.RouteProviders.TryGetValue(chainId, out var providerName)
            && string.Equals(providerName, FakeCustodyProvider.ProviderKey, StringComparison.Ordinal))
        {
            provider = fake;
            return true;
        }

        provider = null!;
        return false;
    }
}

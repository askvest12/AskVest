using AskVest.Modules.Wallets.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets.Custody;

/// <summary>
/// Resolves the custody provider for a chain through the configured routes.
/// The fake serves development and tests; Privy is the real adapter.
/// </summary>
internal sealed class CustodyRegistry(
    IOptions<WalletsOptions> options,
    FakeCustodyProvider fake,
    PrivyCustodyProvider privy)
{
    public bool TryResolve(string chainId, out ICustodyProvider provider)
    {
        if (options.Value.Custody.RouteProviders.TryGetValue(chainId, out var providerName))
        {
            if (string.Equals(providerName, FakeCustodyProvider.ProviderKey, StringComparison.Ordinal))
            {
                provider = fake;
                return true;
            }

            if (string.Equals(providerName, PrivyCustodyProvider.ProviderKey, StringComparison.Ordinal))
            {
                provider = privy;
                return true;
            }
        }

        provider = null!;
        return false;
    }
}

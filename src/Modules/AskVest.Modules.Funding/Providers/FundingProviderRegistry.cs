namespace AskVest.Modules.Funding.Providers;

/// <summary>
/// Resolves the funding provider for a route key. Only fake adapters exist
/// today; each configured provider route gets its own fake instance sharing
/// one deterministic state.
/// </summary>
public sealed class FundingProviderRegistry
{
    private readonly Dictionary<string, IFundingProvider> _providers;

    public FundingProviderRegistry(IEnumerable<IFundingProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Key, StringComparer.Ordinal);
    }

    public bool TryGet(string key, out IFundingProvider? provider)
        => _providers.TryGetValue(key, out provider);

    public IFundingProvider Get(string key)
        => _providers.TryGetValue(key, out var provider)
            ? provider
            : throw new FundingProviderException(FundingProviderFailureKind.NotFound, "provider_unknown");

    public IReadOnlyCollection<string> Keys => _providers.Keys;
}

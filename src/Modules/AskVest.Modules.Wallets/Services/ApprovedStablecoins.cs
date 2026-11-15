using AskVest.Modules.Wallets.Configuration;

namespace AskVest.Modules.Wallets.Services;

/// <summary>
/// The approved stablecoin contracts, populated once at startup by
/// <see cref="StablecoinConfigValidator"/> after every entry passed Catalog
/// address validation. Lookups are by (chain id, canonical contract).
/// </summary>
internal sealed class ApprovedStablecoins
{
    private readonly Dictionary<(string ChainId, string Contract), ApprovedStablecoinOption> _entries =
        new(ValueTupleComparer.Instance);

    public void Replace(IEnumerable<ApprovedStablecoinOption> entries)
    {
        _entries.Clear();
        foreach (var entry in entries)
        {
            _entries[(entry.ChainId, entry.Contract)] = entry;
        }
    }

    public bool TryResolve(string chainId, string contract, out ApprovedStablecoinOption? entry)
        => _entries.TryGetValue((chainId, contract), out entry);

    public IReadOnlyCollection<ApprovedStablecoinOption> Entries => _entries.Values;

    private sealed class ValueTupleComparer : IEqualityComparer<(string ChainId, string Contract)>
    {
        public static readonly ValueTupleComparer Instance = new();

        public bool Equals((string ChainId, string Contract) x, (string ChainId, string Contract) y)
            => string.Equals(x.ChainId, y.ChainId, StringComparison.Ordinal)
                && string.Equals(x.Contract, y.Contract, StringComparison.Ordinal);

        public int GetHashCode((string ChainId, string Contract) obj)
            => HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.ChainId),
                StringComparer.Ordinal.GetHashCode(obj.Contract));
    }
}

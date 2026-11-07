namespace AskVest.Modules.Catalog.Chains;

internal sealed class ChainFamilyRegistry(IEnumerable<IChainFamilyHandler> handlers)
{
    private readonly Dictionary<string, IChainFamilyHandler> _handlers = handlers
        .ToDictionary(handler => handler.Family, StringComparer.Ordinal);

    public bool TryGet(string family, out IChainFamilyHandler? handler)
        => _handlers.TryGetValue(family, out handler);
}

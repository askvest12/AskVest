namespace AskVest.Modules.Catalog.Contracts;

/// <summary>
/// The cross-module view of a catalog chain.
/// </summary>
public sealed record CatalogChain(
    string Id,
    string Family,
    string Name,
    string NativeAssetSymbol,
    int NativeDecimals,
    string ExplorerLinkTemplate,
    int RequiredConfirmations,
    string Status);

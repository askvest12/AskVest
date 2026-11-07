namespace AskVest.Modules.Catalog.Import.Data;

internal sealed record CatalogChainsFile
{
    public required string Version { get; init; }

    public required List<ChainData> Chains { get; init; }
}

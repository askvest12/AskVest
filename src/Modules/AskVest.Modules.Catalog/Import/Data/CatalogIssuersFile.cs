namespace AskVest.Modules.Catalog.Import.Data;

internal sealed record CatalogIssuersFile
{
    public required string Version { get; init; }

    public required List<IssuerData> Issuers { get; init; }
}

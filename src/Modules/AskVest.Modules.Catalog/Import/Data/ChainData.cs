namespace AskVest.Modules.Catalog.Import.Data;

internal sealed record ChainData
{
    public required string Id { get; init; }

    public required string Family { get; init; }

    public required string Name { get; init; }

    public required string NativeAssetSymbol { get; init; }

    public required int NativeDecimals { get; init; }

    public required string ExplorerLinkTemplate { get; init; }

    public required int RequiredConfirmations { get; init; }

    public required string Status { get; init; }
}

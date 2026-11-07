namespace AskVest.Modules.Catalog.Import.Data;

internal sealed record CatalogInstrumentsFile
{
    public required string Version { get; init; }

    public required List<InstrumentData> Instruments { get; init; }
}

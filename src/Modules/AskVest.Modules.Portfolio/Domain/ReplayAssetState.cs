namespace AskVest.Modules.Portfolio.Domain;

/// <summary>
/// Per-asset replay state: token quantity, weighted average cost basis and
/// realized profit for one ledger asset code.
/// </summary>
internal sealed class ReplayAssetState
{
    public required string AssetCode { get; init; }

    public string? InstrumentId { get; set; }

    public decimal Quantity { get; set; }

    public decimal TotalCost { get; set; }

    public decimal RealizedProfit { get; set; }

    public bool HasUnclassifiedMovements { get; set; }
}

using System.Collections.ObjectModel;

namespace AskVest.Modules.Wallets.Configuration;

/// <summary>
/// One stablecoin contract whose deposits credit a cash ledger asset instead
/// of a catalog token. Contract must already be in canonical form; the
/// startup validator proves it through the Catalog address rules.
/// </summary>
public sealed class ApprovedStablecoinOption
{
    public string ChainId { get; set; } = string.Empty;

    public string Contract { get; set; } = string.Empty;

    public string LedgerAssetCode { get; set; } = string.Empty;

    public int Decimals { get; set; }

    /// <summary>
    /// Optional dust floor in asset units; null falls back to the wallets
    /// default dust minimum.
    /// </summary>
    public decimal? DustMinimum { get; set; }

    /// <summary>
    /// Provider asset aliases that resolve to this contract, such as "usdc"
    /// for custody providers that report canonical asset names instead of
    /// contract addresses.
    /// </summary>
    public Collection<string> Aliases { get; } = [];
}

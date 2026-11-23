using System.Collections.ObjectModel;

namespace AskVest.Modules.Wallets.Configuration;

/// <summary>
/// Privy custody adapter settings. AppId and AppSecret are credentials: they
/// come from the secret store in real deployments and are never logged. The
/// treasury wallet funds withdrawals; the owner key quorum is the server
/// signer that controls user wallets when set.
/// </summary>
public sealed class PrivyOptions
{
    public string AppId { get; set; } = string.Empty;

    public string AppSecret { get; set; } = string.Empty;

    public Uri BaseUrl { get; set; } = new("https://api.privy.io");

    /// <summary>Wallet that sends token withdrawals, per chain family.</summary>
    public string TreasuryWalletId { get; set; } = string.Empty;

    /// <summary>
    /// Optional key quorum id owning created user wallets. When empty the
    /// app secret owns them directly.
    /// </summary>
    public string OwnerKeyQuorumId { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Wallets scanned per deposit poll call; the rest wait a cycle.</summary>
    public int WalletScanBatchSize { get; set; } = 10;

    /// <summary>Maximum wallet list pages fetched per refresh.</summary>
    public int WalletListMaxPages { get; set; } = 10;

    /// <summary>Seconds a fetched wallet list stays fresh.</summary>
    public int WalletListRefreshSeconds { get; set; } = 60;

    /// <summary>
    /// Overrides and extensions for the built-in CAIP-2 chain id to Privy
    /// history chain name map. Keys are chain ids like "eip155:8453".
    /// </summary>
    public Dictionary<string, string> HistoryChainNames { get; } = new(StringComparer.Ordinal);
}

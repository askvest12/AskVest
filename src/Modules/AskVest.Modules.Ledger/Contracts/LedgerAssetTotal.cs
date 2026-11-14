namespace AskVest.Modules.Ledger.Contracts;

/// <summary>
/// The summed balance of one asset across every account custody must cover:
/// user available, user held and system fees. System clearing is excluded
/// because clearing balances are offsets, not user property.
/// </summary>
public sealed record LedgerAssetTotal(string AssetCode, decimal Total);

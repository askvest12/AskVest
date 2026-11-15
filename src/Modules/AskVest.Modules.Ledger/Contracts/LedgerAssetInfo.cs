namespace AskVest.Modules.Ledger.Contracts;

/// <summary>Registered asset properties needed by callers that compute amounts.</summary>
public sealed record LedgerAssetInfo(AssetKind Kind, int Scale);

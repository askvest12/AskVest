namespace AskVest.Modules.Catalog.Contracts;

/// <summary>
/// The cross-module view of a catalog token. Status and enums are exposed in
/// their stored string form so consumers never depend on catalog internals.
/// </summary>
public sealed record CatalogToken(
    Guid Id,
    string InstrumentId,
    string IssuerId,
    string ChainId,
    string Standard,
    string CanonicalAddress,
    string Caip19,
    string Symbol,
    int Decimals,
    string LedgerAssetCode,
    string Status,
    string GatewayKey,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? ReviewExpiresAt);

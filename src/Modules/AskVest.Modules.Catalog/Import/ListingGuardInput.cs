namespace AskVest.Modules.Catalog.Import;

/// <summary>
/// Every fact the listing guard needs, already resolved by the importer.
/// </summary>
internal sealed record ListingGuardInput(
    bool IssuerApproved,
    bool ChainEnabled,
    bool ChainFamilySupported,
    bool DisclosuresComplete,
    int AllowedCountryCount,
    bool HasGatewayKey,
    int Decimals,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? ReviewExpiresAt,
    DateTimeOffset Now,
    int ReviewMaxMonths,
    bool InstrumentActive);

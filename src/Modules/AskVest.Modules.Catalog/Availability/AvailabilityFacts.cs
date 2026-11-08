using AskVest.Modules.Catalog.Domain.Enums;

namespace AskVest.Modules.Catalog.Availability;

/// <summary>
/// Every fact the availability rules need, already resolved by the service.
/// </summary>
internal sealed record AvailabilityFacts(
    TokenStatus Status,
    bool IssuerApproved,
    bool ChainEnabled,
    DateTimeOffset? ReviewExpiresAt,
    DateTimeOffset Now,
    IReadOnlySet<string> AllowedCountries,
    string? UserCountry,
    bool HasGatewayKey);

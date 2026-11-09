namespace AskVest.Modules.Catalog.Contracts;

/// <summary>
/// The rights, backing, redemption, fee and risk disclosure text for one
/// token, served verbatim. Cross-module consumers (the assistant) present
/// this data; they never edit it.
/// </summary>
public sealed record CatalogTokenDisclosure(
    Guid TokenId,
    string InstrumentId,
    string Symbol,
    string ChainId,
    string Status,
    string ClaimType,
    string ClaimDescription,
    string BackingDescription,
    string CustodianDescription,
    string RedemptionDescription,
    string DividendPolicy,
    string? DividendNote,
    bool VotingRights,
    string? VotingNote,
    string TransferRestrictions,
    IReadOnlyList<string> IssuerFees,
    bool NoIssuerFees,
    IReadOnlyList<string> RiskStatements,
    string DisclosureDocumentLink,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? ReviewExpiresAt);

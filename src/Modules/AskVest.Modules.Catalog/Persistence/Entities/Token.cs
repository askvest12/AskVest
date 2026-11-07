using AskVest.Modules.Catalog.Domain.Enums;

namespace AskVest.Modules.Catalog.Persistence.Entities;

public sealed class Token
{
    public Guid Id { get; set; }
    public required string InstrumentId { get; set; }
    public required string IssuerId { get; set; }
    public required string ChainId { get; set; }
    public required string Standard { get; set; }
    public required string CanonicalAddress { get; set; }
    public required string Caip19 { get; set; }
    public required string Symbol { get; set; }
    public int Decimals { get; set; }
    public required string LedgerAssetCode { get; set; }
    public TokenStatus Status { get; set; }
    public required string GatewayKey { get; set; }
    public TradingScheduleKind ScheduleKind { get; set; }
    public string? ScheduleTimeZone { get; set; }
    public string? ScheduleNote { get; set; }
    public ClaimType ClaimType { get; set; }
    public required string ClaimDescription { get; set; }
    public required string BackingDescription { get; set; }
    public required string CustodianDescription { get; set; }
    public required string RedemptionDescription { get; set; }
    public DividendPolicy DividendPolicy { get; set; }
    public string? DividendNote { get; set; }
    public bool VotingRights { get; set; }
    public string? VotingNote { get; set; }
    public required string TransferRestrictions { get; set; }
    public List<string> IssuerFees { get; set; } = [];
    public bool NoIssuerFees { get; set; }
    public List<string> RiskStatements { get; set; } = [];
    public required string DisclosureDocumentLink { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? ReviewExpiresAt { get; set; }
    public string? ReviewerLabel { get; set; }
    public bool EverListed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

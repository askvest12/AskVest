namespace AskVest.Modules.Catalog.Import.Data;

internal sealed record TokenData
{
    public required string IssuerId { get; init; }

    public required string ChainId { get; init; }

    public required string Standard { get; init; }

    public required string Address { get; init; }

    public required string Symbol { get; init; }

    public required int Decimals { get; init; }

    public required string LedgerAssetCode { get; init; }

    public required string Status { get; init; }

    public bool EverListed { get; init; }

    public string? GatewayKey { get; init; }

    public required TradingScheduleData Schedule { get; init; }

    public string? ExternalTransfers { get; init; }

    public required string ClaimType { get; init; }

    public required string ClaimDescription { get; init; }

    public required string BackingDescription { get; init; }

    public required string CustodianDescription { get; init; }

    public required string RedemptionDescription { get; init; }

    public required string DividendPolicy { get; init; }

    public string? DividendNote { get; init; }

    public required bool VotingRights { get; init; }

    public string? VotingNote { get; init; }

    public required string TransferRestrictions { get; init; }

    public List<string> IssuerFees { get; init; } = [];

    public bool NoIssuerFees { get; init; }

    public List<string> RiskStatements { get; init; } = [];

    public required string DisclosureDocumentLink { get; init; }

    public List<string> AllowedCountries { get; init; } = [];

    public DateTimeOffset? ReviewedAt { get; init; }

    public DateTimeOffset? ReviewExpiresAt { get; init; }

    public string? ReviewerLabel { get; init; }
}

namespace AskVest.Modules.Funding.Providers;

/// <summary>A provider-issued funding instruction for one user and currency.</summary>
public sealed record FundingInstruction(
    string ProviderReference,
    string InstructionJson);

/// <summary>One provider deposit as reported by list or webhook.</summary>
public sealed record FundingDepositItem(
    string ProviderDepositId,
    string AccountReference,
    string Currency,
    decimal Amount,
    decimal ProviderFee,
    bool PayerVerified,
    FundingDepositReportState State,
    DateTimeOffset ObservedAt);

public enum FundingDepositReportState
{
    Pending,
    Completed,
    Failed,
    Reversed,
}

public sealed record FundingDepositPage(
    IReadOnlyList<FundingDepositItem> Items,
    string? NextCursor);

/// <summary>Provider answer to a beneficiary add; details never return.</summary>
public sealed record FundingBeneficiaryResult(
    string ProviderReference,
    string MaskedDisplay,
    bool NameMatch);

public enum FundingPayoutReportState
{
    Pending,
    Completed,
    Failed,
    NotFound,
}

public sealed record FundingPayoutStatus(
    FundingPayoutReportState State,
    string? DetailCode);

public enum FundingWebhookKind
{
    Deposit,
    Payout,
}

/// <summary>A normalized provider webhook event; payload content stays inside the module.</summary>
public sealed record FundingWebhookEventData(
    string EventId,
    FundingWebhookKind Kind,
    FundingDepositItem? Deposit,
    string? PayoutKey,
    FundingPayoutReportState? PayoutState,
    string? PayoutDetailCode);

public enum FundingWebhookParseStatus
{
    Ok,
    MissingOrStaleTimestamp,
    InvalidSignature,
    Malformed,
}

public sealed record FundingWebhookParseResult(
    FundingWebhookParseStatus Status,
    FundingWebhookEventData? Event);

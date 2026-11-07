namespace AskVest.Modules.Compliance.Providers;

internal enum KycWebhookOutcome
{
    Approved,
    Rejected,
}

internal enum KycWebhookParseStatus
{
    Ok,
    MissingOrStaleTimestamp,
    InvalidSignature,
    Malformed,
}

/// <summary>
/// Normalized provider decision. Name and date of birth exist only in memory
/// for the screening call and are never persisted.
/// </summary>
internal sealed record KycWebhookEventData(
    string EventId,
    string ProviderReference,
    KycWebhookOutcome Outcome,
    string? RejectionCode,
    string? VerifiedCountry,
    string? FullName,
    DateOnly? DateOfBirth);

internal sealed record KycWebhookParseResult(KycWebhookParseStatus Status, KycWebhookEventData? EventData);

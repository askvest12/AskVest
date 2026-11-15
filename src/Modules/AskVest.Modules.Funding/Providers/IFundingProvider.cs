namespace AskVest.Modules.Funding.Providers;

/// <summary>
/// The only payment/payout provider seam. Adapters own vendor specifics;
/// callers see provider-agnostic records. Provider failures surface as
/// <see cref="FundingProviderException"/> with a classified kind.
/// </summary>
public interface IFundingProvider
{
    /// <summary>Route key matching Funding:Providers and Funding:Currencies.</summary>
    string Key { get; }

    /// <summary>Creates or returns the funding instruction for a user reference and currency.</summary>
    Task<FundingInstruction> GetOrCreateInstructionAsync(
        string userReference,
        string currency,
        CancellationToken cancellationToken = default);

    /// <summary>Lists provider deposits after an opaque cursor, newest last.</summary>
    Task<FundingDepositPage> ListDepositsAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a beneficiary. Details are passed through to the provider
    /// and never stored, logged or returned.
    /// </summary>
    Task<FundingBeneficiaryResult> AddBeneficiaryAsync(
        string userReference,
        string currency,
        IReadOnlyDictionary<string, string> details,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a payout idempotent by our withdrawal key. A definitive
    /// rejection throws <see cref="FundingProviderException"/> with kind
    /// Rejected; transport ambiguity throws Transient or RateLimited.
    /// </summary>
    Task RequestPayoutAsync(
        string withdrawalKey,
        string beneficiaryReference,
        string currency,
        decimal amount,
        CancellationToken cancellationToken = default);

    /// <summary>Polls payout status by our withdrawal key.</summary>
    Task<FundingPayoutStatus> GetPayoutStatusAsync(
        string withdrawalKey,
        CancellationToken cancellationToken = default);

    /// <summary>Reports the provider settlement balance for a currency.</summary>
    Task<decimal> GetSettlementBalanceAsync(
        string currency,
        CancellationToken cancellationToken = default);

    /// <summary>Verifies the signature and normalizes a webhook payload.</summary>
    FundingWebhookParseResult ParseWebhook(
        string rawBody,
        IReadOnlyDictionary<string, string?> headers,
        DateTimeOffset now);
}

public enum FundingProviderFailureKind
{
    Transient,
    RateLimited,
    Rejected,
    NotFound,
    Authentication,
}

/// <summary>
/// A classified provider failure. Rejected carries a provider reason code;
/// the others carry none.
/// </summary>
public sealed class FundingProviderException : Exception
{
    public FundingProviderException()
        : base("Funding provider call failed.")
    {
    }

    public FundingProviderException(string message)
        : base(message)
    {
    }

    public FundingProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FundingProviderException(FundingProviderFailureKind kind, string? reasonCode = null)
        : base($"Funding provider call failed: {kind}.")
    {
        Kind = kind;
        ReasonCode = reasonCode;
    }

    public FundingProviderFailureKind Kind { get; }

    public string? ReasonCode { get; }
}

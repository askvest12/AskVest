namespace AskVest.Modules.Compliance.Domain;

/// <summary>
/// Stable reason codes reported by the eligibility function and surfaced in
/// 403 compliance.not_eligible error details.
/// </summary>
public static class BlockerCodes
{
    public const string EmailNotVerified = "email_not_verified";
    public const string AccountNotActive = "account_not_active";
    public const string CountryRequired = "country_required";
    public const string CountryNotSupported = "country_not_supported";
    public const string ConsentRequired = "consent_required";
    public const string RealMoneyDisabled = "real_money_disabled";
    public const string KycRequired = "kyc_required";
    public const string KycInReview = "kyc_in_review";
    public const string KycRejected = "kyc_rejected";
    public const string KycExpired = "kyc_expired";
    public const string KycAttemptsExhausted = "kyc_attempts_exhausted";
    public const string AccountRestricted = "account_restricted";
}

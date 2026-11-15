using System.Globalization;

namespace AskVest.Modules.Funding.Domain;

/// <summary>
/// Database text mapping for the funding state enums. Values are stable
/// strings; never rename a stored value without a migration.
/// </summary>
public static class FundingText
{
    public static string ToDb(DepositState state) => state switch
    {
        DepositState.Pending => "pending",
        DepositState.Credited => "credited",
        DepositState.Quarantined => "quarantined",
        DepositState.Failed => "failed",
        DepositState.Reversed => "reversed",
        DepositState.Ignored => "ignored",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static DepositState DepositStateFromDb(string value) => value switch
    {
        "pending" => DepositState.Pending,
        "credited" => DepositState.Credited,
        "quarantined" => DepositState.Quarantined,
        "failed" => DepositState.Failed,
        "reversed" => DepositState.Reversed,
        "ignored" => DepositState.Ignored,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToDb(ConversionState state) => state switch
    {
        ConversionState.Quoted => "quoted",
        ConversionState.Completed => "completed",
        ConversionState.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static ConversionState ConversionStateFromDb(string value) => value switch
    {
        "quoted" => ConversionState.Quoted,
        "completed" => ConversionState.Completed,
        "expired" => ConversionState.Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToDb(BeneficiaryState state) => state switch
    {
        BeneficiaryState.CoolingOff => "cooling_off",
        BeneficiaryState.Active => "active",
        BeneficiaryState.Removed => "removed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static BeneficiaryState BeneficiaryStateFromDb(string value) => value switch
    {
        "cooling_off" => BeneficiaryState.CoolingOff,
        "active" => BeneficiaryState.Active,
        "removed" => BeneficiaryState.Removed,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToDb(WithdrawalState state) => state switch
    {
        WithdrawalState.Previewed => "previewed",
        WithdrawalState.Requested => "requested",
        WithdrawalState.UnderReview => "under_review",
        WithdrawalState.Submitting => "submitting",
        WithdrawalState.Submitted => "submitted",
        WithdrawalState.Completed => "completed",
        WithdrawalState.Failed => "failed",
        WithdrawalState.Cancelled => "cancelled",
        WithdrawalState.NeedsReconciliation => "needs_reconciliation",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static WithdrawalState WithdrawalStateFromDb(string value) => value switch
    {
        "previewed" => WithdrawalState.Previewed,
        "requested" => WithdrawalState.Requested,
        "under_review" => WithdrawalState.UnderReview,
        "submitting" => WithdrawalState.Submitting,
        "submitted" => WithdrawalState.Submitted,
        "completed" => WithdrawalState.Completed,
        "failed" => WithdrawalState.Failed,
        "cancelled" => WithdrawalState.Cancelled,
        "needs_reconciliation" => WithdrawalState.NeedsReconciliation,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToDb(FundingEventType type) => type switch
    {
        FundingEventType.FundingAccountCreated => "funding_account_created",
        FundingEventType.DepositStateChanged => "deposit_state_changed",
        FundingEventType.ConversionCompleted => "conversion_completed",
        FundingEventType.BeneficiaryStateChanged => "beneficiary_state_changed",
        FundingEventType.WithdrawalStateChanged => "withdrawal_state_changed",
        FundingEventType.RestrictionAdded => "restriction_added",
        FundingEventType.RestrictionCleared => "restriction_cleared",
        FundingEventType.PauseChanged => "pause_changed",
        FundingEventType.OperatorAction => "operator_action",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static FundingEventType EventTypeFromDb(string value) => value switch
    {
        "funding_account_created" => FundingEventType.FundingAccountCreated,
        "deposit_state_changed" => FundingEventType.DepositStateChanged,
        "conversion_completed" => FundingEventType.ConversionCompleted,
        "beneficiary_state_changed" => FundingEventType.BeneficiaryStateChanged,
        "withdrawal_state_changed" => FundingEventType.WithdrawalStateChanged,
        "restriction_added" => FundingEventType.RestrictionAdded,
        "restriction_cleared" => FundingEventType.RestrictionCleared,
        "pause_changed" => FundingEventType.PauseChanged,
        "operator_action" => FundingEventType.OperatorAction,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>
    /// Canonical decimal text used inside terms hashes so the hash is
    /// stable across cultures and trailing zeros.
    /// </summary>
    public static string Canonical(decimal value)
        => value.ToString("G29", CultureInfo.InvariantCulture);
}

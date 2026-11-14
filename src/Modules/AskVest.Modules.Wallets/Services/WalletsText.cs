using AskVest.Modules.Wallets.Domain;

namespace AskVest.Modules.Wallets.Services;

internal static class WalletsText
{
    public static string ToDb(WithdrawalState state) => state switch
    {
        WithdrawalState.Previewed => "previewed",
        WithdrawalState.Requested => "requested",
        WithdrawalState.Submitting => "submitting",
        WithdrawalState.Submitted => "submitted",
        WithdrawalState.Completed => "completed",
        WithdrawalState.Failed => "failed",
        WithdrawalState.Cancelled => "cancelled",
        _ => "needs_reconciliation",
    };

    public static string ToDb(DepositState state) => state switch
    {
        DepositState.Pending => "pending",
        DepositState.Credited => "credited",
        DepositState.Quarantined => "quarantined",
        _ => "ignored",
    };

    public static string ToDb(WithdrawalAddressState state) => state switch
    {
        WithdrawalAddressState.CoolingOff => "cooling_off",
        WithdrawalAddressState.Active => "active",
        _ => "removed",
    };

    public static string ToDb(WalletEventType type) => type switch
    {
        WalletEventType.DepositCredited => "deposit_credited",
        WalletEventType.WithdrawalAddressAdded => "withdrawal_address_added",
        _ => "withdrawal_state_changed",
    };

    public static WithdrawalState WithdrawalStateFromDb(string value) => value switch
    {
        "previewed" => WithdrawalState.Previewed,
        "requested" => WithdrawalState.Requested,
        "submitting" => WithdrawalState.Submitting,
        "submitted" => WithdrawalState.Submitted,
        "completed" => WithdrawalState.Completed,
        "failed" => WithdrawalState.Failed,
        "cancelled" => WithdrawalState.Cancelled,
        _ => WithdrawalState.NeedsReconciliation,
    };

    public static DepositState DepositStateFromDb(string value) => value switch
    {
        "pending" => DepositState.Pending,
        "credited" => DepositState.Credited,
        "quarantined" => DepositState.Quarantined,
        _ => DepositState.Ignored,
    };

    public static WithdrawalAddressState WithdrawalAddressStateFromDb(string value) => value switch
    {
        "cooling_off" => WithdrawalAddressState.CoolingOff,
        "active" => WithdrawalAddressState.Active,
        _ => WithdrawalAddressState.Removed,
    };

    public static WalletEventType EventTypeFromDb(string value) => value switch
    {
        "deposit_credited" => WalletEventType.DepositCredited,
        "withdrawal_address_added" => WalletEventType.WithdrawalAddressAdded,
        _ => WalletEventType.WithdrawalStateChanged,
    };
}

namespace AskVest.Modules.Wallets.Domain;

public enum WithdrawalState
{
    Previewed,
    Requested,
    Submitting,
    Submitted,
    Completed,
    Failed,
    Cancelled,
    NeedsReconciliation,
}

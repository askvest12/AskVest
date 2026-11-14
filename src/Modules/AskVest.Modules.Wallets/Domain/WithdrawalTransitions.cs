namespace AskVest.Modules.Wallets.Domain;

/// <summary>
/// The single authority on legal withdrawal transitions. Anything not listed
/// here is a bug: callers refuse it and log. Terminal states have no outgoing
/// transitions.
/// </summary>
public static class WithdrawalTransitions
{
    private static readonly HashSet<(WithdrawalState From, WithdrawalState To)> Legal = new()
    {
        (WithdrawalState.Previewed, WithdrawalState.Requested),
        (WithdrawalState.Requested, WithdrawalState.Submitting),
        (WithdrawalState.Requested, WithdrawalState.Cancelled),
        (WithdrawalState.Submitting, WithdrawalState.Submitted),
        (WithdrawalState.Submitting, WithdrawalState.Failed),
        (WithdrawalState.Submitting, WithdrawalState.NeedsReconciliation),
        (WithdrawalState.Submitted, WithdrawalState.Completed),
        (WithdrawalState.Submitted, WithdrawalState.Failed),
        (WithdrawalState.Submitted, WithdrawalState.NeedsReconciliation),
        (WithdrawalState.NeedsReconciliation, WithdrawalState.Completed),
        (WithdrawalState.NeedsReconciliation, WithdrawalState.Failed),
    };

    public static bool CanTransition(WithdrawalState from, WithdrawalState to)
        => Legal.Contains((from, to));

    /// <summary>
    /// Completed, failed and cancelled are final. NeedsReconciliation is not
    /// final: a definitive custody answer can still settle or fail it.
    /// </summary>
    public static bool IsFinal(WithdrawalState state)
        => state is WithdrawalState.Completed or WithdrawalState.Failed or WithdrawalState.Cancelled;

    /// <summary>
    /// Withdrawals still holding user funds: everything that is not final.
    /// </summary>
    public static bool IsNonFinal(WithdrawalState state) => !IsFinal(state);

    /// <summary>
    /// States counted toward the rolling 24 hour limit: every committed
    /// withdrawal except failed and cancelled. Previews are not commitments.
    /// </summary>
    public static bool CountsTowardRollingTotal(WithdrawalState state)
        => state is WithdrawalState.Requested or WithdrawalState.Submitting
            or WithdrawalState.Submitted or WithdrawalState.Completed
            or WithdrawalState.NeedsReconciliation;
}

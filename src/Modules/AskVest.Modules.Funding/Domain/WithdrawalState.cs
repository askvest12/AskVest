namespace AskVest.Modules.Funding.Domain;

public enum WithdrawalState
{
    Previewed,
    Requested,
    UnderReview,
    Submitting,
    Submitted,
    Completed,
    Failed,
    Cancelled,
    NeedsReconciliation,
}

/// <summary>
/// The single authority on legal cash withdrawal transitions. Anything not
/// listed here is a bug: callers refuse it and log. Completed, failed and
/// cancelled are final; needs_reconciliation keeps funds held until a
/// definitive provider answer arrives.
/// </summary>
public static class WithdrawalTransitions
{
    private static readonly HashSet<(WithdrawalState From, WithdrawalState To)> Legal = new()
    {
        (WithdrawalState.Previewed, WithdrawalState.Requested),
        (WithdrawalState.Previewed, WithdrawalState.UnderReview),
        (WithdrawalState.Requested, WithdrawalState.Submitting),
        (WithdrawalState.Requested, WithdrawalState.Cancelled),
        (WithdrawalState.UnderReview, WithdrawalState.Requested),
        (WithdrawalState.UnderReview, WithdrawalState.Failed),
        (WithdrawalState.Submitting, WithdrawalState.Submitted),
        (WithdrawalState.Submitting, WithdrawalState.Failed),
        (WithdrawalState.Submitting, WithdrawalState.NeedsReconciliation),
        // Recovery only: the payout job returns a stuck submission for retry.
        (WithdrawalState.Submitting, WithdrawalState.Requested),
        (WithdrawalState.Submitted, WithdrawalState.Completed),
        (WithdrawalState.Submitted, WithdrawalState.Failed),
        (WithdrawalState.Submitted, WithdrawalState.NeedsReconciliation),
        (WithdrawalState.NeedsReconciliation, WithdrawalState.Completed),
        (WithdrawalState.NeedsReconciliation, WithdrawalState.Failed),
    };

    public static bool CanTransition(WithdrawalState from, WithdrawalState to)
        => Legal.Contains((from, to));

    public static bool IsFinal(WithdrawalState state)
        => state is WithdrawalState.Completed or WithdrawalState.Failed or WithdrawalState.Cancelled;

    public static bool IsNonFinal(WithdrawalState state) => !IsFinal(state);

    /// <summary>
    /// States counted toward the rolling withdrawal caps: every committed
    /// withdrawal except failed and cancelled. Previews are not commitments.
    /// </summary>
    public static bool CountsTowardRollingTotal(WithdrawalState state)
        => state is WithdrawalState.Requested or WithdrawalState.UnderReview
            or WithdrawalState.Submitting or WithdrawalState.Submitted
            or WithdrawalState.Completed or WithdrawalState.NeedsReconciliation;
}

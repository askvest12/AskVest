namespace AskVest.Modules.Trading.Domain;

/// <summary>
/// The single authority on legal order transitions. Anything not listed here
/// is a bug: callers refuse it and log. Terminal states have no outgoing
/// transitions.
/// </summary>
public static class OrderTransitions
{
    private static readonly HashSet<(OrderState From, OrderState To)> Legal = new()
    {
        (OrderState.PendingSubmission, OrderState.Submitting),
        (OrderState.PendingSubmission, OrderState.Cancelled),
        (OrderState.Submitting, OrderState.Submitted),
        (OrderState.Submitting, OrderState.Failed),
        (OrderState.Submitting, OrderState.NeedsReconciliation),
        (OrderState.Submitted, OrderState.Executed),
        (OrderState.Submitted, OrderState.Failed),
        (OrderState.Submitted, OrderState.NeedsReconciliation),
        (OrderState.NeedsReconciliation, OrderState.Executed),
        (OrderState.NeedsReconciliation, OrderState.Failed),
    };

    public static bool CanTransition(OrderState from, OrderState to)
        => Legal.Contains((from, to));

    /// <summary>
    /// Executed, failed and cancelled are final. NeedsReconciliation is not
    /// final: a definitive gateway answer can still settle or fail it.
    /// </summary>
    public static bool IsFinal(OrderState state)
        => state is OrderState.Executed or OrderState.Failed or OrderState.Cancelled;

    /// <summary>
    /// Orders still holding user funds: everything that is not final. This is
    /// the set the open-order cap counts.
    /// </summary>
    public static bool IsNonFinal(OrderState state) => !IsFinal(state);

    /// <summary>
    /// States counted toward the rolling 24 hour limit: every order except
    /// failed and cancelled.
    /// </summary>
    public static bool CountsTowardRollingTotal(OrderState state)
        => state is not (OrderState.Failed or OrderState.Cancelled);
}

namespace AskVest.Modules.Funding.Domain;

public enum DepositState
{
    Pending,
    Credited,
    Quarantined,
    Failed,
    Reversed,
    Ignored,
}

/// <summary>
/// Deposit lifecycle. Pending rows may still resolve to any outcome;
/// credited may only move to reversed; the rest are terminal except
/// quarantined, which an operator can still credit or fail.
/// </summary>
public static class DepositTransitions
{
    public static bool CanTransition(DepositState from, DepositState to)
        => (from, to) switch
        {
            (DepositState.Pending, DepositState.Credited) => true,
            (DepositState.Pending, DepositState.Quarantined) => true,
            (DepositState.Pending, DepositState.Failed) => true,
            (DepositState.Pending, DepositState.Ignored) => true,
            (DepositState.Credited, DepositState.Reversed) => true,
            (DepositState.Quarantined, DepositState.Credited) => true,
            (DepositState.Quarantined, DepositState.Failed) => true,
            _ => false,
        };

    public static bool IsTerminal(DepositState state)
        => state is DepositState.Credited or DepositState.Failed
            or DepositState.Reversed or DepositState.Ignored;
}

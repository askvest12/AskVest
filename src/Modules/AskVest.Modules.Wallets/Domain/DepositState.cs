namespace AskVest.Modules.Wallets.Domain;

public enum DepositState
{
    Pending,
    Credited,
    Quarantined,
    Ignored,
}

/// <summary>
/// Deposit lifecycle. Pending rows may still resolve to any outcome;
/// credited, quarantined and ignored are terminal.
/// </summary>
public static class DepositTransitions
{
    public static bool CanTransition(DepositState from, DepositState to)
        => from == DepositState.Pending
            && to is DepositState.Credited or DepositState.Quarantined or DepositState.Ignored;

    public static bool IsTerminal(DepositState state)
        => state is DepositState.Credited or DepositState.Quarantined or DepositState.Ignored;
}

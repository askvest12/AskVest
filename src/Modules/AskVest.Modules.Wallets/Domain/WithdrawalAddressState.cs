namespace AskVest.Modules.Wallets.Domain;

public enum WithdrawalAddressState
{
    CoolingOff,
    Active,
    Removed,
}

/// <summary>
/// Address book lifecycle. A new address cools off before it can receive
/// withdrawals; removal is allowed from either live state and is terminal.
/// </summary>
public static class WithdrawalAddressTransitions
{
    public static bool CanTransition(WithdrawalAddressState from, WithdrawalAddressState to)
        => (from, to) switch
        {
            (WithdrawalAddressState.CoolingOff, WithdrawalAddressState.Active) => true,
            (WithdrawalAddressState.CoolingOff, WithdrawalAddressState.Removed) => true,
            (WithdrawalAddressState.Active, WithdrawalAddressState.Removed) => true,
            _ => false,
        };

    public static bool IsTerminal(WithdrawalAddressState state)
        => state == WithdrawalAddressState.Removed;
}

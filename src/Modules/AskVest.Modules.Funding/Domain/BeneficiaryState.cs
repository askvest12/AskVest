namespace AskVest.Modules.Funding.Domain;

public enum BeneficiaryState
{
    CoolingOff,
    Active,
    Removed,
}

/// <summary>
/// Beneficiaries cool off before first use; removal is always allowed and
/// terminal.
/// </summary>
public static class BeneficiaryTransitions
{
    public static bool CanTransition(BeneficiaryState from, BeneficiaryState to)
        => (from, to) switch
        {
            (BeneficiaryState.CoolingOff, BeneficiaryState.Active) => true,
            (BeneficiaryState.CoolingOff, BeneficiaryState.Removed) => true,
            (BeneficiaryState.Active, BeneficiaryState.Removed) => true,
            _ => false,
        };

    public static bool IsTerminal(BeneficiaryState state)
        => state == BeneficiaryState.Removed;
}

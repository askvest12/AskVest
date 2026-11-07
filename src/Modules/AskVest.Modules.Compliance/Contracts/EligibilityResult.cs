namespace AskVest.Modules.Compliance.Contracts;

/// <summary>
/// The eligibility answer for one user. Real implies every Practice condition;
/// RealBlockers contains the Practice blockers plus the Real-only ones.
/// </summary>
public sealed record EligibilityResult(
    bool PracticeAllowed,
    bool RealAllowed,
    IReadOnlyList<EligibilityBlocker> PracticeBlockers,
    IReadOnlyList<EligibilityBlocker> RealBlockers);

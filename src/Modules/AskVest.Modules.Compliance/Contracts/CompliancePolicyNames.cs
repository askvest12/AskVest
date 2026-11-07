namespace AskVest.Modules.Compliance.Contracts;

/// <summary>
/// Authorization policy names applied to the Practice and Real route groups.
/// The host registers them; the architecture tests prove every endpoint under
/// the groups carries the matching policy.
/// </summary>
public static class CompliancePolicyNames
{
    public const string PracticeEligible = "PracticeEligible";
    public const string RealEligible = "RealEligible";
}

using AskVest.Modules.Compliance.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace AskVest.Modules.Compliance.Authorization;

/// <summary>
/// Marks an authorization policy as backed by the eligibility gate. The policy
/// name selects which blocker list applies.
/// </summary>
internal sealed class EligibilityRequirement(string policyName) : IAuthorizationRequirement
{
    public string PolicyName { get; } = policyName;

    public bool IsReal => PolicyName == CompliancePolicyNames.RealEligible;
}

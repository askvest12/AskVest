namespace AskVest.Modules.Compliance.Contracts;

/// <summary>
/// Read-only eligibility view for other modules. Phases 6, 10 and 12 consume
/// it; nothing may bypass the route-group policies it backs.
/// </summary>
public interface IComplianceEligibility
{
    Task<EligibilityResult> GetEligibilityAsync(Guid userId, CancellationToken cancellationToken = default);

    bool IsRealMoneyCountry(string countryCode);
}

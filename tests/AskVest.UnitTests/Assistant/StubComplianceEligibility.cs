using AskVest.Modules.Compliance.Contracts;

namespace AskVest.UnitTests.Assistant;

internal sealed class StubComplianceEligibility : IComplianceEligibility
{
    public bool RealAllowed { get; set; }

    public Task<EligibilityResult> GetEligibilityAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult(new EligibilityResult(
            PracticeAllowed: true,
            RealAllowed: RealAllowed,
            PracticeBlockers: [],
            RealBlockers: RealAllowed ? [] : [new EligibilityBlocker("kyc_pending", "KYC review pending.")]));

    public Task<IReadOnlyList<string>> GetMissingDocumentTypesAsync(
        Guid userId, string requiredFor, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> GetEffectiveCountryAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public bool IsRealMoneyCountry(string countryCode) => true;
}

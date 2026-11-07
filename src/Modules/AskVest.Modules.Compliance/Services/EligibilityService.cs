using AskVest.Modules.Compliance.Configuration;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Compliance.Domain;
using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence;
using AskVest.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Compliance.Services;

internal sealed class EligibilityService(
    ComplianceDbContext db,
    IUserDirectory userDirectory,
    IOptions<ComplianceOptions> options,
    TimeProvider timeProvider) : IComplianceEligibility
{
    public async Task<EligibilityResult> GetEligibilityAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var account = await userDirectory.FindAsync(userId, cancellationToken);
        var profile = await db.ComplianceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        var acceptedKeys = await db.ConsentRecords.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => c.DocumentType + "@" + c.Version)
            .ToListAsync(cancellationToken);

        return EligibilityEvaluator.Evaluate(
            account,
            profile,
            acceptedKeys.ToHashSet(StringComparer.Ordinal),
            options.Value,
            timeProvider.GetUtcNow());
    }

    public bool IsRealMoneyCountry(string countryCode)
        => options.Value.RealMoneyCountries.Contains(countryCode, StringComparer.Ordinal);
}

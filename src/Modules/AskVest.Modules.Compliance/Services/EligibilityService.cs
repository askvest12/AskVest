using AskVest.Modules.Compliance.Configuration;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Compliance.Domain;
using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence;
using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Learning.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Compliance.Services;

internal sealed class EligibilityService(
    ComplianceDbContext db,
    IUserDirectory userDirectory,
    ILearningRequirements learningRequirements,
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

        var incompleteRequired = await learningRequirements
            .GetIncompleteRequiredLessonsAsync(userId, cancellationToken);

        return EligibilityEvaluator.Evaluate(
            account,
            profile,
            acceptedKeys.ToHashSet(StringComparer.Ordinal),
            options.Value,
            timeProvider.GetUtcNow(),
            incompleteRequired.Select(lesson => lesson.LessonId).ToList());
    }

    public async Task<IReadOnlyList<string>> GetMissingDocumentTypesAsync(
        Guid userId,
        string requiredFor,
        CancellationToken cancellationToken = default)
    {
        var acceptedKeys = await db.ConsentRecords.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => c.DocumentType + "@" + c.Version)
            .ToListAsync(cancellationToken);
        var accepted = acceptedKeys.ToHashSet(StringComparer.Ordinal);

        return options.Value.Documents
            .Where(document => document.RequiredFor == requiredFor)
            .Where(document => !accepted.Contains(EligibilityEvaluator.ConsentKey(document.Type, document.Version)))
            .Select(document => document.Type)
            .ToList();
    }

    public async Task<string?> GetEffectiveCountryAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var profile = await db.ComplianceProfiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.VerifiedCountry, p.DeclaredCountry })
            .FirstOrDefaultAsync(cancellationToken);
        return profile?.VerifiedCountry ?? profile?.DeclaredCountry;
    }

    public bool IsRealMoneyCountry(string countryCode)
        => options.Value.RealMoneyCountries.Contains(countryCode, StringComparer.Ordinal);
}

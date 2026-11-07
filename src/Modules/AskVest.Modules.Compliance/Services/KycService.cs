using AskVest.Modules.Compliance.Configuration;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Compliance.Domain;
using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence;
using AskVest.Modules.Compliance.Providers;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Compliance.Services;

internal sealed record KycSessionStart(string ProviderReference, string SessionUrl, string KycStatus);

/// <summary>
/// Starts or resumes provider verification. Every precondition failure is a
/// compliance.not_eligible answer with one detail per blocker.
/// </summary>
internal sealed class KycService(
    ComplianceDbContext db,
    ProfileStore profileStore,
    IComplianceEligibility eligibility,
    IKycProvider kycProvider,
    IOptions<ComplianceOptions> options,
    TimeProvider timeProvider)
{
    public async Task<Result<KycSessionStart>> StartAsync(
        Guid userId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var result = await eligibility.GetEligibilityAsync(userId, cancellationToken);
        var profile = await db.ComplianceProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        var country = profile?.VerifiedCountry ?? profile?.DeclaredCountry;

        var blockers = new List<EligibilityBlocker>();
        if (!result.PracticeAllowed)
        {
            blockers.AddRange(result.PracticeBlockers);
        }

        if (country is null || !eligibility.IsRealMoneyCountry(country))
        {
            blockers.Add(new EligibilityBlocker(
                BlockerCodes.CountryNotSupported,
                "Real money is not available in this country."));
        }

        var realMissing = await MissingRealConsentsAsync(userId, cancellationToken);
        if (realMissing.Count > 0)
        {
            blockers.Add(new EligibilityBlocker(
                BlockerCodes.ConsentRequired,
                $"Accept the current documents: {string.Join(", ", realMissing)}."));
        }

        if (blockers.Count > 0)
        {
            return Result.Failure<KycSessionStart>(NotEligible(blockers));
        }

        if (profile!.KycStatus == KycStatus.Approved && profile.ExpiresAt > timeProvider.GetUtcNow())
        {
            return Result.Failure<KycSessionStart>(new Error(
                ErrorCodes.BusinessRuleViolated,
                "Identity verification is already approved.",
                ErrorType.BusinessRule));
        }

        if (profile.KycStatus == KycStatus.InReview && profile.ProviderReference is not null)
        {
            var resume = await kycProvider.ResumeSessionAsync(profile.ProviderReference, cancellationToken);
            if (resume.IsFailure)
            {
                return Result.Failure<KycSessionStart>(ServiceUnavailableError());
            }

            return Result.Success(new KycSessionStart(
                resume.Value.ProviderReference,
                resume.Value.SessionUrl,
                nameof(KycStatus.InReview)));
        }

        if (profile.AttemptsUsed >= options.Value.Kyc.MaxAttempts)
        {
            return Result.Failure<KycSessionStart>(new Error(
                ErrorCodes.ComplianceKycAttemptsExhausted,
                "The verification attempt limit was reached.",
                ErrorType.Conflict));
        }

        var session = await kycProvider.StartVerificationAsync(userId, country!, cancellationToken);
        if (session.IsFailure)
        {
            return Result.Failure<KycSessionStart>(ServiceUnavailableError());
        }

        var now = timeProvider.GetUtcNow();
        profile.KycStatus = KycStatus.InReview;
        profile.KycProvider = kycProvider.Name;
        profile.ProviderReference = session.Value.ProviderReference;
        profile.AttemptsUsed += 1;
        profile.RejectionCode = null;
        profile.UpdatedAt = now;
        profileStore.AddEvent(
            userId,
            ComplianceEventType.KycStarted,
            correlationId,
            $"provider={kycProvider.Name}");
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success(new KycSessionStart(
            session.Value.ProviderReference,
            session.Value.SessionUrl,
            nameof(KycStatus.InReview)));
    }

    private async Task<List<string>> MissingRealConsentsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var acceptedKeys = (await db.ConsentRecords.AsNoTracking()
                .Where(c => c.UserId == userId)
                .Select(c => c.DocumentType + "@" + c.Version)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        return options.Value.Documents
            .Where(document => document.RequiredFor == "real")
            .Where(document => !acceptedKeys.Contains(EligibilityEvaluator.ConsentKey(document.Type, document.Version)))
            .Select(document => document.Type)
            .ToList();
    }

    private static Error NotEligible(IReadOnlyList<EligibilityBlocker> blockers)
        => new(
            ErrorCodes.ComplianceNotEligible,
            "The account is not eligible for real money verification.",
            ErrorType.Forbidden,
            blockers.Select(blocker => new ValidationFailure(blocker.Code, blocker.Message)).ToList());

    private static Error ServiceUnavailableError()
        => new(
            ErrorCodes.ServiceUnavailable,
            "The verification provider is unavailable.",
            ErrorType.Unavailable);
}

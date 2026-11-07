using AskVest.Modules.Compliance.Configuration;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence.Entities;
using AskVest.Modules.Identity.Contracts;

namespace AskVest.Modules.Compliance.Domain;

/// <summary>
/// Pure eligibility table from docs/design/compliance.md. Callers load the
/// profile, the accepted consent keys and the account snapshot; this function
/// only decides.
/// </summary>
public static class EligibilityEvaluator
{
    public static EligibilityResult Evaluate(
        UserAccountSnapshot? account,
        ComplianceProfile? profile,
        IReadOnlySet<string> acceptedConsentKeys,
        ComplianceOptions options,
        DateTimeOffset now)
    {
        var practiceBlockers = new List<EligibilityBlocker>();
        if (account is null || !account.IsActive)
        {
            practiceBlockers.Add(Blocker(BlockerCodes.AccountNotActive, "The account is not active."));
        }
        else if (!account.EmailVerified)
        {
            practiceBlockers.Add(Blocker(BlockerCodes.EmailNotVerified, "The email address is not verified."));
        }

        var country = profile?.VerifiedCountry ?? profile?.DeclaredCountry;
        var blockedCountries = options.BlockedCountries.ToHashSet(StringComparer.Ordinal);
        if (country is null)
        {
            practiceBlockers.Add(Blocker(BlockerCodes.CountryRequired, "A country of residence must be declared."));
        }
        else if (blockedCountries.Contains(country))
        {
            practiceBlockers.Add(Blocker(BlockerCodes.CountryNotSupported, "AskVest is not available in this country."));
        }

        var practiceMissing = MissingConsents(options, acceptedConsentKeys, "practice");
        if (practiceMissing.Count > 0)
        {
            practiceBlockers.Add(Blocker(
                BlockerCodes.ConsentRequired,
                $"Accept the current documents: {string.Join(", ", practiceMissing)}."));
        }

        var realBlockers = new List<EligibilityBlocker>(practiceBlockers);
        if (!options.RealMoneyEnabled)
        {
            realBlockers.Add(Blocker(BlockerCodes.RealMoneyDisabled, "Real money is not enabled."));
        }

        if (country is not null && !blockedCountries.Contains(country)
            && !options.RealMoneyCountries.Contains(country, StringComparer.Ordinal))
        {
            realBlockers.Add(Blocker(BlockerCodes.CountryNotSupported, "Real money is not available in this country."));
        }

        var realMissing = MissingConsents(options, acceptedConsentKeys, "real");
        if (realMissing.Count > 0)
        {
            realBlockers.Add(Blocker(
                BlockerCodes.ConsentRequired,
                $"Accept the current documents: {string.Join(", ", realMissing)}."));
        }

        if (KycBlocker(profile, options, now) is { } kycBlocker)
        {
            realBlockers.Add(kycBlocker);
        }

        if (profile?.ScreeningStatus == ScreeningStatus.Hit)
        {
            realBlockers.Add(Blocker(BlockerCodes.AccountRestricted, "The account is restricted."));
        }

        return new EligibilityResult(
            practiceBlockers.Count == 0,
            realBlockers.Count == 0,
            practiceBlockers,
            realBlockers);
    }

    private static List<string> MissingConsents(
        ComplianceOptions options,
        IReadOnlySet<string> acceptedConsentKeys,
        string requiredFor)
    {
        var missing = new List<string>();
        foreach (var document in options.Documents)
        {
            if (document.RequiredFor != requiredFor)
            {
                continue;
            }

            if (!acceptedConsentKeys.Contains(ConsentKey(document.Type, document.Version)))
            {
                missing.Add(document.Type);
            }
        }

        return missing;
    }

    private static EligibilityBlocker? KycBlocker(ComplianceProfile? profile, ComplianceOptions options, DateTimeOffset now)
    {
        if (profile is null || profile.KycStatus == KycStatus.NotStarted)
        {
            return Blocker(BlockerCodes.KycRequired, "Identity verification has not been started.");
        }

        if (profile.KycStatus == KycStatus.InReview)
        {
            return Blocker(BlockerCodes.KycInReview, "Identity verification is in review.");
        }

        if (profile.KycStatus == KycStatus.Approved && profile.ExpiresAt > now)
        {
            return null;
        }

        if (profile.AttemptsUsed >= options.Kyc.MaxAttempts)
        {
            return Blocker(BlockerCodes.KycAttemptsExhausted, "The verification attempt limit was reached.");
        }

        return profile.KycStatus switch
        {
            KycStatus.Approved => Blocker(BlockerCodes.KycExpired, "The identity verification has expired."),
            KycStatus.Rejected => Blocker(BlockerCodes.KycRejected, "Identity verification was rejected."),
            _ => Blocker(BlockerCodes.KycRequired, "Identity verification has not been started."),
        };
    }

    public static string ConsentKey(string documentType, string version)
        => $"{documentType}@{version}";

    private static EligibilityBlocker Blocker(string code, string message)
        => new(code, message);
}

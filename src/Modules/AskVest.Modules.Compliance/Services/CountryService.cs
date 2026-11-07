using AskVest.Modules.Compliance.Domain;
using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Compliance.Services;

internal sealed class CountryService(ComplianceDbContext db, ProfileStore profileStore, TimeProvider timeProvider)
{
    public async Task<Result> SetCountryAsync(
        Guid userId,
        string country,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var normalized = country.Trim().ToUpperInvariant();
        if (!CountryCodes.IsValid(normalized))
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "The country code is not a valid ISO 3166-1 alpha-2 code.",
                ErrorType.Validation,
                [new ValidationFailure("country", "The country code is not valid.")]));
        }

        var profile = await profileStore.GetOrCreateAsync(userId, cancellationToken);
        if (profile.KycStatus == KycStatus.Approved)
        {
            return Result.Failure(new Error(
                ErrorCodes.BusinessRuleViolated,
                "The country of residence cannot be changed after verification approval.",
                ErrorType.BusinessRule));
        }

        if (string.Equals(profile.DeclaredCountry, normalized, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        var isFirstDeclaration = profile.DeclaredCountry is null;
        profile.DeclaredCountry = normalized;
        profile.UpdatedAt = timeProvider.GetUtcNow();
        profileStore.AddEvent(
            userId,
            isFirstDeclaration ? ComplianceEventType.CountrySet : ComplianceEventType.CountryChanged,
            correlationId,
            $"country={normalized}");
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Identity.Services;

public sealed record OnboardingProfileInfo(
    string? Familiarity,
    IReadOnlyList<string> Goals,
    string? PreferredCurrency,
    DateTimeOffset? CompletedAt);

public sealed class OnboardingService(IdentityDbContext db, TimeProvider timeProvider)
{
    private static readonly Error _invalid = new(
        ErrorCodes.ValidationFailed,
        "The request is not valid.",
        ErrorType.Validation);

    public async Task<Result<OnboardingProfileInfo?>> SaveAsync(
        Guid userId,
        OnboardingRequest request,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<OnboardingProfileInfo?>(new Error(
                ErrorCodes.ResourceNotFound,
                "The resource was not found.",
                ErrorType.NotFound));
        }

        if (request.FullName is { } fullName)
        {
            var trimmed = fullName.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 120)
            {
                return Result.Failure<OnboardingProfileInfo?>(_invalid);
            }

            user.FullName = trimmed;
            user.UpdatedAt = timeProvider.GetUtcNow();
        }

        var profile = await db.OnboardingProfiles.FindAsync([userId], cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (profile is null)
        {
            profile = new OnboardingProfile
            {
                UserId = userId,
                CreatedAt = now,
            };
            db.OnboardingProfiles.Add(profile);
        }

        if (request.Familiarity is { } familiarityWire)
        {
            if (!OnboardingWire.TryParseFamiliarity(familiarityWire, out var familiarity))
            {
                return Result.Failure<OnboardingProfileInfo?>(_invalid);
            }

            profile.InvestingFamiliarity = familiarity;
        }

        if (request.Goals is { } goals)
        {
            if (goals.Any(goal => !OnboardingWire.IsKnownGoal(goal)))
            {
                return Result.Failure<OnboardingProfileInfo?>(_invalid);
            }

            profile.GoalFlags = OnboardingWire.GoalsToFlags(goals);
        }

        if (request.PreferredCurrency is { } currency)
        {
            var trimmedCurrency = currency.Trim().ToUpperInvariant();
            if (trimmedCurrency.Length != 3 || !trimmedCurrency.All(char.IsLetter))
            {
                return Result.Failure<OnboardingProfileInfo?>(_invalid);
            }

            profile.PreferredCurrency = trimmedCurrency;
        }

        profile.UpdatedAt = now;
        if (profile.InvestingFamiliarity is not null && profile.GoalFlags != 0)
        {
            profile.CompletedAt ??= now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result<OnboardingProfileInfo?>.Success(ToInfo(profile));
    }

    public static OnboardingProfileInfo? ToInfo(OnboardingProfile? profile)
    {
        if (profile is null)
        {
            return null;
        }

        return new OnboardingProfileInfo(
            profile.InvestingFamiliarity is { } familiarity ? OnboardingWire.FamiliarityToWire(familiarity) : null,
            OnboardingWire.FlagsToGoals(profile.GoalFlags),
            profile.PreferredCurrency,
            profile.CompletedAt);
    }
}

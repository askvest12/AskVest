using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.SharedKernel;

namespace AskVest.Modules.Compliance.Providers;

/// <summary>
/// External sanctions screening boundary. A failure is a Result failure so the
/// webhook can answer 503 and let the provider retry.
/// </summary>
internal interface IScreeningProvider
{
    Task<Result<ScreeningDecision>> ScreenAsync(
        string fullName,
        DateOnly dateOfBirth,
        string? country,
        CancellationToken cancellationToken);
}

internal sealed record ScreeningDecision(ScreeningStatus Outcome, string? Reference);

using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.SharedKernel;

namespace AskVest.Modules.Compliance.Providers;

/// <summary>
/// Deterministic development screening provider. Returns a hit when the full
/// name contains the configured test marker.
/// </summary>
internal sealed class FakeScreeningProvider(string? hitNameMarker) : IScreeningProvider
{
    public Task<Result<ScreeningDecision>> ScreenAsync(
        string fullName,
        DateOnly dateOfBirth,
        string? country,
        CancellationToken cancellationToken)
    {
        var hit = hitNameMarker is not null
            && fullName.Contains(hitNameMarker, StringComparison.OrdinalIgnoreCase);
        var decision = new ScreeningDecision(
            hit ? ScreeningStatus.Hit : ScreeningStatus.Clear,
            $"fake-screening-{Guid.CreateVersion7():N}");
        return Task.FromResult(Result.Success(decision));
    }
}

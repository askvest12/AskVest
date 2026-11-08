using AskVest.Modules.MarketData.Domain;
using AskVest.Modules.MarketData.Domain.Enums;

namespace AskVest.Modules.MarketData.Services;

internal static class FreshnessLabeler
{
    public static FreshnessAssessment Evaluate(
        int delayMinutes,
        string? session,
        DateTimeOffset? asOf,
        DateTimeOffset now,
        int liveThresholdSeconds,
        int staleThresholdSeconds)
    {
        if (asOf is null)
        {
            return new FreshnessAssessment(FreshnessLabel.Unavailable, 0, false);
        }

        var normalizedSession = string.IsNullOrWhiteSpace(session) ? "unknown" : session;
        var effectiveAge = now - asOf.Value - TimeSpan.FromMinutes(delayMinutes);
        if (effectiveAge < TimeSpan.Zero)
        {
            effectiveAge = TimeSpan.Zero;
        }

        var ageSeconds = (long)effectiveAge.TotalSeconds;
        FreshnessLabel label;
        if (normalizedSession == "halted")
        {
            label = FreshnessLabel.Halted;
        }
        else if (normalizedSession == "closed")
        {
            label = FreshnessLabel.Closed;
        }
        else if (ageSeconds > staleThresholdSeconds)
        {
            label = FreshnessLabel.Stale;
        }
        else if (delayMinutes == 0 && ageSeconds <= liveThresholdSeconds)
        {
            label = FreshnessLabel.Live;
        }
        else
        {
            label = FreshnessLabel.Delayed;
        }

        var usable = (label is FreshnessLabel.Live or FreshnessLabel.Delayed)
            && normalizedSession == "regular";
        return new FreshnessAssessment(label, ageSeconds, usable);
    }
}

using AskVest.Modules.Practice.Configuration;

namespace AskVest.Modules.Practice.Services;

internal static class PracticeResetPolicy
{
    public static readonly TimeSpan ResetWindow = TimeSpan.FromDays(30);

    public static PracticeResetDecision Evaluate(
        DateTimeOffset lastActivityAt,
        DateTimeOffset now,
        int resetsInWindow,
        PracticeOptions options)
    {
        var cooldown = TimeSpan.FromHours(options.ResetCooldownHours);
        var nextAllowedAt = lastActivityAt + cooldown;
        var resetsLeft = Math.Max(0, options.MaxResetsPer30Days - resetsInWindow);

        if (now < nextAllowedAt)
        {
            return new PracticeResetDecision(false, "cooldown_not_elapsed", nextAllowedAt, resetsLeft);
        }

        if (resetsInWindow >= options.MaxResetsPer30Days)
        {
            return new PracticeResetDecision(false, "reset_limit_reached", nextAllowedAt, 0);
        }

        return new PracticeResetDecision(true, null, nextAllowedAt, resetsLeft);
    }

    internal readonly record struct PracticeResetDecision(
        bool Allowed,
        string? Reason,
        DateTimeOffset NextAllowedAt,
        int ResetsLeft);
}

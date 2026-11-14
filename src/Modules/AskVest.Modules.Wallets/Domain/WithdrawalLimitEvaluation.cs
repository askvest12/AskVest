namespace AskVest.Modules.Wallets.Domain;

/// <summary>
/// The conservative withdrawal limits, all configurable. Values are USD
/// amounts derived from the net quantity times the market reference price;
/// the limit check runs under a per-user lock inside the placement
/// transaction.
/// </summary>
public sealed record WithdrawalLimits(
    decimal MinQuantity,
    decimal MaxValueUsd,
    decimal Rolling24HourMaxValueUsd);

/// <summary>
/// Rolling usage already committed for the user, read inside the placement
/// transaction under the per-user lock.
/// </summary>
public sealed record WithdrawalUsage(decimal Rolling24HourValueUsd);

public enum WithdrawalLimitViolation
{
    None,
    BelowMinimum,
    AboveMaximumValue,
    RollingLimit,
}

public static class WithdrawalLimitEvaluation
{
    /// <summary>
    /// Evaluates a prospective withdrawal against the configured limits and
    /// the user's current usage. The prospective withdrawal itself counts
    /// toward the rolling total.
    /// </summary>
    public static WithdrawalLimitViolation Evaluate(
        WithdrawalLimits limits,
        WithdrawalUsage usage,
        decimal netQuantity,
        decimal netValueUsd)
    {
        if (netQuantity < limits.MinQuantity)
        {
            return WithdrawalLimitViolation.BelowMinimum;
        }

        if (netValueUsd > limits.MaxValueUsd)
        {
            return WithdrawalLimitViolation.AboveMaximumValue;
        }

        if (usage.Rolling24HourValueUsd + netValueUsd > limits.Rolling24HourMaxValueUsd)
        {
            return WithdrawalLimitViolation.RollingLimit;
        }

        return WithdrawalLimitViolation.None;
    }
}

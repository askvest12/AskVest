namespace AskVest.Modules.Trading.Domain;

/// <summary>
/// The conservative trading limits, all configurable. The limit check and the
/// order insert run under a per-user lock so parallel orders cannot exceed
/// them.
/// </summary>
public sealed record TradingLimits(
    decimal MinOrderAmount,
    decimal MaxOrderAmount,
    decimal Rolling24HourMax,
    int MaxNonFinalOrders);

/// <summary>
/// Rolling usage already committed for the user, read inside the placement
/// transaction under the per-user lock.
/// </summary>
public sealed record TradingUsage(
    decimal Rolling24HourTotal,
    int NonFinalOrderCount);

public enum LimitViolation
{
    None,
    BelowMinimum,
    AboveMaximum,
    RollingLimit,
    TooManyOpenOrders,
}

public static class LimitEvaluation
{
    /// <summary>
    /// Evaluates a prospective order total against the configured limits and
    /// the user's current usage. The prospective order itself counts toward
    /// both totals.
    /// </summary>
    public static LimitViolation Evaluate(TradingLimits limits, TradingUsage usage, decimal orderTotal)
    {
        if (orderTotal < limits.MinOrderAmount)
        {
            return LimitViolation.BelowMinimum;
        }

        if (orderTotal > limits.MaxOrderAmount)
        {
            return LimitViolation.AboveMaximum;
        }

        if (usage.Rolling24HourTotal + orderTotal > limits.Rolling24HourMax)
        {
            return LimitViolation.RollingLimit;
        }

        if (usage.NonFinalOrderCount + 1 > limits.MaxNonFinalOrders)
        {
            return LimitViolation.TooManyOpenOrders;
        }

        return LimitViolation.None;
    }
}

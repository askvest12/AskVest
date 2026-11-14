using AskVest.Modules.Wallets.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Wallets;

public sealed class WithdrawalLimitEvaluationTests
{
    private static readonly WithdrawalLimits Limits = new(
        MinQuantity: 0.5m,
        MaxValueUsd: 100m,
        Rolling24HourMaxValueUsd: 250m);

    private static readonly WithdrawalUsage Empty = new(Rolling24HourValueUsd: 0m);

    [Fact]
    public void Within_All_Limits_Passes()
        => WithdrawalLimitEvaluation.Evaluate(Limits, Empty, 5m, 50m)
            .ShouldBe(WithdrawalLimitViolation.None);

    [Fact]
    public void Quantity_At_Minimum_Passes()
        => WithdrawalLimitEvaluation.Evaluate(Limits, Empty, 0.5m, 5m)
            .ShouldBe(WithdrawalLimitViolation.None);

    [Fact]
    public void Quantity_Below_Minimum_Fails()
        => WithdrawalLimitEvaluation.Evaluate(Limits, Empty, 0.49m, 5m)
            .ShouldBe(WithdrawalLimitViolation.BelowMinimum);

    [Fact]
    public void Value_At_Maximum_Passes()
        => WithdrawalLimitEvaluation.Evaluate(Limits, Empty, 5m, 100m)
            .ShouldBe(WithdrawalLimitViolation.None);

    [Fact]
    public void Value_Above_Maximum_Fails()
        => WithdrawalLimitEvaluation.Evaluate(Limits, Empty, 5m, 100.01m)
            .ShouldBe(WithdrawalLimitViolation.AboveMaximumValue);

    [Fact]
    public void Rolling_Total_At_Limit_Passes()
        => WithdrawalLimitEvaluation.Evaluate(
                Limits, new WithdrawalUsage(200m), 5m, 50m)
            .ShouldBe(WithdrawalLimitViolation.None);

    [Fact]
    public void Rolling_Total_Above_Limit_Fails()
        => WithdrawalLimitEvaluation.Evaluate(
                Limits, new WithdrawalUsage(200.01m), 5m, 50m)
            .ShouldBe(WithdrawalLimitViolation.RollingLimit);

    [Fact]
    public void Minimum_Is_Checked_Before_Value_Limits()
        => WithdrawalLimitEvaluation.Evaluate(Limits, Empty, 0.1m, 500m)
            .ShouldBe(WithdrawalLimitViolation.BelowMinimum);
}

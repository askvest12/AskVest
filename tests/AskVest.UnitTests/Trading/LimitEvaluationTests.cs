using System.Globalization;
using AskVest.Modules.Trading.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Trading;

public sealed class LimitEvaluationTests
{
    private static readonly TradingLimits Limits = new(
        MinOrderAmount: 5m,
        MaxOrderAmount: 1000m,
        Rolling24HourMax: 2000m,
        MaxNonFinalOrders: 5);

    private static readonly TradingUsage Empty = new(0m, 0);

    public static TheoryData<string, LimitViolation> OrderAmountBoundaries()
    {
        var data = new TheoryData<string, LimitViolation>();
        data.Add("4.999999", LimitViolation.BelowMinimum);
        data.Add("5", LimitViolation.None);
        data.Add("1000", LimitViolation.None);
        data.Add("1000.000001", LimitViolation.AboveMaximum);
        return data;
    }

    [Theory]
    [MemberData(nameof(OrderAmountBoundaries))]
    public void Order_Amount_Boundaries(string totalText, LimitViolation expected)
    {
        var total = decimal.Parse(totalText, CultureInfo.InvariantCulture);
        LimitEvaluation.Evaluate(Limits, Empty, total).ShouldBe(expected);
    }

    [Fact]
    public void Rolling_Total_Boundary_Is_Inclusive()
    {
        var usage = new TradingUsage(1500m, 0);
        LimitEvaluation.Evaluate(Limits, usage, 500m).ShouldBe(LimitViolation.None);
        LimitEvaluation.Evaluate(Limits, usage, 500.000001m).ShouldBe(LimitViolation.RollingLimit);
    }

    [Fact]
    public void Open_Order_Cap_Boundary_Is_Inclusive()
    {
        var usage = new TradingUsage(0m, 4);
        LimitEvaluation.Evaluate(Limits, usage, 10m).ShouldBe(LimitViolation.None);
        LimitEvaluation.Evaluate(Limits, new TradingUsage(0m, 5), 10m)
            .ShouldBe(LimitViolation.TooManyOpenOrders);
    }

    [Fact]
    public void Amount_Checks_Run_Before_Usage_Checks()
    {
        var usage = new TradingUsage(5000m, 99);
        LimitEvaluation.Evaluate(Limits, usage, 1m).ShouldBe(LimitViolation.BelowMinimum);
        LimitEvaluation.Evaluate(Limits, usage, 5000m).ShouldBe(LimitViolation.AboveMaximum);
    }
}

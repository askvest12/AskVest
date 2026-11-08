using AskVest.Modules.Practice.Services;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Practice;

public sealed class PracticeOrderMathTests
{
    [Fact]
    public void Buy_Quantity_Rounds_Down_So_Cost_Never_Exceeds_The_Amount()
    {
        // 100 USD at 30.07 -> 3.3258... -> 3.325800 at scale 6, cost 100.001406? no:
        // quantity = floor(100/30.07 * 1e6)/1e6 must satisfy qty*price <= 100.
        var computation = PracticeOrderMath.ComputeBuy(100m, 30.07m, 6, 0);
        computation.Quantity.ShouldBe(3.325573m);
        (computation.Quantity * 30.07m).ShouldBeLessThanOrEqualTo(100m);
        computation.Cost.ShouldBe(PracticeRounding.RoundMoney(computation.Quantity * 30.07m));
    }

    [Fact]
    public void Buy_Fee_Is_Cost_Times_Basis_Points_Rounded()
    {
        var computation = PracticeOrderMath.ComputeBuy(1000m, 50m, 6, 50);
        computation.Quantity.ShouldBe(20m);
        computation.Cost.ShouldBe(1000m);
        computation.Fee.ShouldBe(5m);
    }

    [Fact]
    public void Buy_Tiny_Amount_Rounds_Quantity_To_Zero()
    {
        var computation = PracticeOrderMath.ComputeBuy(0.000001m, 100m, 6, 0);
        computation.Quantity.ShouldBe(0m);
    }

    [Fact]
    public void Sell_Proceeds_And_Fee_Use_The_Shared_Rounding()
    {
        var computation = PracticeOrderMath.ComputeSell(2.5m, 33.333333m, 100);
        computation.Proceeds.ShouldBe(83.333333m);
        computation.Fee.ShouldBe(0.833333m);
    }

    [Fact]
    public void Sell_Fee_Rounds_Half_Away_From_Zero()
    {
        var computation = PracticeOrderMath.ComputeSell(1m, 10.005m, 50);
        computation.Proceeds.ShouldBe(10.005000m);
        computation.Fee.ShouldBe(0.050025m);
    }
}

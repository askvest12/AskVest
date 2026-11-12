using System.Globalization;
using AskVest.Modules.Trading.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Trading;

public sealed class TradingFeeMathTests
{
    public static TheoryData<string, int, string> FeeCases()
    {
        var data = new TheoryData<string, int, string>();
        data.Add("100", 0, "0");
        data.Add("100", 100, "1");
        data.Add("100", 25, "0.25");
        data.Add("33.333333", 15, "0.05");
        data.Add("0.005", 1, "0.000001");
        return data;
    }

    [Theory]
    [MemberData(nameof(FeeCases))]
    public void OurFee_Is_Basis_Points_On_Gross_Rounded_Half_Away_From_Zero(
        string grossText, int bps, string expectedText)
    {
        var gross = decimal.Parse(grossText, CultureInfo.InvariantCulture);
        var expected = decimal.Parse(expectedText, CultureInfo.InvariantCulture);
        TradingFeeMath.OurFee(gross, bps).ShouldBe(expected);
    }

    [Fact]
    public void Fee_Rounds_Half_Away_From_Zero()
    {
        TradingFeeMath.RoundMoney(0.0000005m).ShouldBe(0.000001m);
        TradingFeeMath.RoundMoney(-0.0000005m).ShouldBe(-0.000001m);
    }

    [Fact]
    public void Buy_Total_Adds_All_Fees()
        => TradingFeeMath.BuyTotal(100m, 1m, 0.5m).ShouldBe(101.5m);

    [Fact]
    public void Sell_Total_Subtracts_All_Fees()
        => TradingFeeMath.SellTotal(100m, 1m, 0.5m).ShouldBe(98.5m);
}

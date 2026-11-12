using AskVest.Modules.Trading.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Trading;

public sealed class FillComparisonTests
{
    private static readonly QuoteTerms BuyTerms = new(
        OrderSide.Buy, 2m, 50m, 100m, 1m, 0.5m, 101.5m);
    private static readonly QuoteTerms SellTerms = new(
        OrderSide.Sell, 2m, 50m, 100m, 1m, 0.5m, 98.5m);

    private static GatewayFillTerms Fill(
        decimal quantity = 2m,
        decimal price = 50m,
        decimal gross = 100m,
        decimal gatewayFee = 0.5m,
        decimal? total = null,
        OrderSide side = OrderSide.Buy)
        => new(
            quantity,
            price,
            gross,
            gatewayFee,
            total ?? (side == OrderSide.Buy ? gross + gatewayFee : gross - gatewayFee),
            "chain-1",
            "ref-1");

    [Fact]
    public void Exact_Buy_Fill_Matches()
        => FillComparison.Matches(Fill(), BuyTerms).ShouldBeTrue();

    [Fact]
    public void Exact_Sell_Fill_Matches()
        => FillComparison.Matches(Fill(side: OrderSide.Sell), SellTerms).ShouldBeTrue();

    public static TheoryData<string> QuantityDifferences()
        => new() { "2.000000000000000001", "1.999999999999999999", "3" };

    [Theory]
    [MemberData(nameof(QuantityDifferences))]
    public void Any_Difference_Is_A_Mismatch(string quantityText)
    {
        var quantity = decimal.Parse(quantityText, System.Globalization.CultureInfo.InvariantCulture);
        FillComparison.Matches(Fill(quantity: quantity), BuyTerms).ShouldBeFalse();
        FillComparison.DescribeMismatch(Fill(quantity: quantity), BuyTerms).ShouldContain("quantity");
    }

    [Fact]
    public void Price_Difference_Is_A_Mismatch()
    {
        FillComparison.Matches(Fill(price: 50.01m), BuyTerms).ShouldBeFalse();
        FillComparison.DescribeMismatch(Fill(price: 50.01m), BuyTerms).ShouldBe("price");
    }

    [Fact]
    public void Gross_Difference_Is_A_Mismatch()
    {
        FillComparison.Matches(Fill(gross: 100.01m), BuyTerms).ShouldBeFalse();
        FillComparison.DescribeMismatch(Fill(gross: 100.01m), BuyTerms).ShouldContain("gross_amount");
    }

    [Fact]
    public void Gateway_Fee_Difference_Is_A_Mismatch()
    {
        FillComparison.Matches(Fill(gatewayFee: 0.6m), BuyTerms).ShouldBeFalse();
        FillComparison.DescribeMismatch(Fill(gatewayFee: 0.6m), BuyTerms).ShouldContain("gateway_fee");
    }

    [Fact]
    public void Total_Difference_Is_A_Mismatch()
    {
        FillComparison.Matches(Fill(total: 101.6m), BuyTerms).ShouldBeFalse();
        FillComparison.DescribeMismatch(Fill(total: 101.6m), BuyTerms).ShouldBe("total");
    }

    [Fact]
    public void Settlement_References_Do_Not_Affect_Matching()
    {
        var fill = Fill() with { SettlementChainId = "other", SettlementReference = "other-ref" };
        FillComparison.Matches(fill, BuyTerms).ShouldBeTrue();
    }
}

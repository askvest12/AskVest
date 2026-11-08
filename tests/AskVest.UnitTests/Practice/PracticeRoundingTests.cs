using AskVest.Modules.Practice.Services;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Practice;

public sealed class PracticeRoundingTests
{
    [Theory]
    [InlineData("10", 6, "10")]
    [InlineData("1.23456789", 6, "1.234567")]
    [InlineData("0.0000009", 6, "0.000000")]
    [InlineData("3.141592653589793238462643383279", 6, "3.141592")]
    public void RoundQuantity_Truncates_To_Scale(string input, int scale, string expected)
    {
        PracticeRounding.RoundQuantity(decimal.Parse(input), scale).ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData("1.2345674", "1.234567")]
    [InlineData("1.2345675", "1.234568")]
    [InlineData("2.5", "2.5")]
    [InlineData("0.0000005", "0.000001")]
    public void RoundMoney_Rounds_Half_Away_From_Zero(string input, string expected)
    {
        PracticeRounding.RoundMoney(decimal.Parse(input)).ToString().ShouldBe(expected);
    }

    [Fact]
    public void RoundDisplay_Rounds_To_Two_Decimals_Half_Up()
    {
        PracticeRounding.RoundDisplay(1.005m).ShouldBe(1.01m);
        PracticeRounding.RoundDisplay(1.004m).ShouldBe(1.00m);
        PracticeRounding.RoundDisplay(1234.565m).ShouldBe(1234.57m);
    }
}

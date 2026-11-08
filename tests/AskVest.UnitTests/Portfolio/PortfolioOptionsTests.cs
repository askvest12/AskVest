using AskVest.Modules.Portfolio.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Portfolio;

public sealed class PortfolioOptionsTests
{
    [Fact]
    public void Defaults_Are_Valid()
    {
        var result = PortfolioOptionsValidator.Instance.Validate(null, new PortfolioOptions());
        result.Failed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public void Invalid_Watchlist_Maximum_Fails(int maximum)
    {
        var options = new PortfolioOptions { WatchlistMaximum = maximum };
        PortfolioOptionsValidator.Instance.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Invalid_Replay_Cap_Fails(int cap)
    {
        var options = new PortfolioOptions { MaxJournalsPerReplay = cap };
        PortfolioOptionsValidator.Instance.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void Invalid_Display_Decimals_Fails(int decimals)
    {
        var options = new PortfolioOptions { DisplayDecimals = decimals };
        PortfolioOptionsValidator.Instance.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Empty_Cash_Equivalents_Fail()
    {
        var options = new PortfolioOptions();
        options.CashEquivalentAssets.Clear();
        PortfolioOptionsValidator.Instance.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Overlong_Cash_Equivalent_Fails()
    {
        var options = new PortfolioOptions();
        options.CashEquivalentAssets.Clear();
        options.CashEquivalentAssets.Add(new string('A', 17));
        PortfolioOptionsValidator.Instance.Validate(null, options).Failed.ShouldBeTrue();
    }
}

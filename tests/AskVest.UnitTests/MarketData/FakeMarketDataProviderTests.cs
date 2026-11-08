using AskVest.Modules.MarketData.Configuration;
using AskVest.Modules.MarketData.Providers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.MarketData;

public sealed class FakeMarketDataProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static FakeMarketDataProvider Create(
        string scenario = "normal",
        FakeProviderOptions? fakeOptions = null,
        DateTimeOffset? now = null)
    {
        var options = Options.Create(new MarketDataOptions
        {
            Fake = fakeOptions ?? new FakeProviderOptions { Scenario = scenario },
        });
        var clock = new FakeTimeProvider(now ?? Now);
        var provider = new FakeMarketDataProvider(options, clock);
        provider.SetScenario(scenario);
        return provider;
    }

    [Fact]
    public async Task Same_Symbol_And_Time_Return_Same_Price()
    {
        var provider = Create();
        var first = await provider.GetLatestQuotesAsync(["EX:AAA"], TestContext.Current.CancellationToken);
        var second = await provider.GetLatestQuotesAsync(["EX:AAA"], TestContext.Current.CancellationToken);

        first.Quotes[0].Price.ShouldBe(second.Quotes[0].Price);
        first.Quotes[0].AsOf.ShouldBe(second.Quotes[0].AsOf);
    }

    [Fact]
    public async Task Different_Symbols_Get_Different_Prices()
    {
        var provider = Create();
        var result = await provider.GetLatestQuotesAsync(["EX:AAA", "EX:BBB", "EX:CCC"], TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ProviderOutcome.Succeeded);
        result.Quotes.Count.ShouldBe(3);
        result.Quotes.Select(q => q.Price).Distinct().Count().ShouldBeGreaterThan(1);
        result.Quotes.ShouldAllBe(q => q.Price > 0);
        result.Quotes.ShouldAllBe(q => q.Session == "regular");
        result.Quotes.ShouldAllBe(q => q.Currency == "USD");
        result.UnknownSymbols.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delayed_Scenario_Declares_Delay_And_Regular_Session()
    {
        var provider = Create("delayed", new FakeProviderOptions { Scenario = "delayed", DelayMinutes = 15 });
        provider.Capabilities.DelayMinutes.ShouldBe(15);

        var result = await provider.GetLatestQuotesAsync(["EX:AAA"], TestContext.Current.CancellationToken);
        result.Outcome.ShouldBe(ProviderOutcome.Succeeded);
        result.Quotes[0].Session.ShouldBe("regular");
    }

    [Fact]
    public async Task Halted_And_Closed_Scenarios_Report_Sessions()
    {
        var halted = await Create("halted").GetLatestQuotesAsync(["EX:AAA"], TestContext.Current.CancellationToken);
        halted.Quotes[0].Session.ShouldBe("halted");

        var closed = await Create("closed").GetLatestQuotesAsync(["EX:AAA"], TestContext.Current.CancellationToken);
        closed.Quotes[0].Session.ShouldBe("closed");
        closed.Quotes[0].PreviousClose.ShouldNotBeNull();
    }

    [Fact]
    public async Task Normal_Scenario_Has_No_Previous_Close()
    {
        var result = await Create().GetLatestQuotesAsync(["EX:AAA"], TestContext.Current.CancellationToken);
        result.Quotes[0].PreviousClose.ShouldBeNull();
    }

    [Fact]
    public async Task Outlier_Spike_Scenario_Multiplies_Price()
    {
        var normal = await Create("normal").GetLatestQuotesAsync(["EX:AAA"], TestContext.Current.CancellationToken);
        var spike = await Create("outlier_spike").GetLatestQuotesAsync(["EX:AAA"], TestContext.Current.CancellationToken);

        spike.Quotes[0].Price.ShouldBeGreaterThan(normal.Quotes[0].Price * 3m);
        spike.Quotes[0].Session.ShouldBe("regular");
    }

    [Fact]
    public async Task Drop_Scenario_Lowers_Every_Price_Below_Outlier_Tolerance()
    {
        var symbols = new[] { "EX:AAA", "EX:BBB" };
        var normal = await Create("normal").GetLatestQuotesAsync(symbols, TestContext.Current.CancellationToken);
        var drop = await Create("drop", new FakeProviderOptions { Scenario = "drop", DropPercent = 20 })
            .GetLatestQuotesAsync(symbols, TestContext.Current.CancellationToken);

        drop.Outcome.ShouldBe(ProviderOutcome.Succeeded);
        drop.Quotes.Count.ShouldBe(2);
        for (var i = 0; i < symbols.Length; i++)
        {
            var ratio = drop.Quotes[i].Price / normal.Quotes[i].Price;
            drop.Quotes[i].Session.ShouldBe("regular");
            ratio.ShouldBeLessThan(0.85m);
            ratio.ShouldBeGreaterThan(0.70m);
        }
    }

    [Fact]
    public async Task Outage_Scenario_Fails_As_Transient()
    {
        var result = await Create("outage").GetLatestQuotesAsync(["EX:AAA"], TestContext.Current.CancellationToken);
        result.Outcome.ShouldBe(ProviderOutcome.TransientFailure);
        result.Quotes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rate_Limited_Scenario_Returns_Retry_Delay()
    {
        var result = await Create("rate_limited", new FakeProviderOptions { Scenario = "rate_limited", RateLimitRetrySeconds = 7 })
            .GetLatestQuotesAsync(["EX:AAA"], TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ProviderOutcome.RateLimited);
        result.RetryAfter.ShouldBe(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task Unknown_Symbol_Scenario_Reports_Only_Unknown_Marked_Symbols()
    {
        var result = await Create("unknown_symbol")
            .GetLatestQuotesAsync(["EX:AAA", "EX:UNKNOWN-B", "EX:CCC"], TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ProviderOutcome.Succeeded);
        result.Quotes.Select(q => q.ProviderSymbol).ShouldBe(["EX:AAA", "EX:CCC"]);
        result.UnknownSymbols.ShouldBe(["EX:UNKNOWN-B"]);
    }

    [Fact]
    public async Task Candles_Are_Ascending_Valid_And_Adjusted()
    {
        var provider = Create();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(2);
        var result = await provider.GetCandlesAsync("EX:AAA", "1d", from, to, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ProviderOutcome.Succeeded);
        result.Candles.Count.ShouldBe(3);
        result.Candles.Select(c => c.OpenTime).ShouldBe(result.Candles.Select(c => c.OpenTime).OrderBy(t => t));
        result.Candles.ShouldAllBe(c => c.Open > 0 && c.High > 0 && c.Low > 0 && c.Close > 0);
        result.Candles.ShouldAllBe(c => c.High >= c.Low);
        result.Candles.ShouldAllBe(c => c.Adjusted);
        result.Candles.ShouldAllBe(c => c.Volume >= 0);
    }

    [Fact]
    public async Task Same_Candle_Request_Is_Deterministic()
    {
        var provider = Create();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = await provider.GetCandlesAsync("EX:AAA", "1h", from, from.AddHours(3), TestContext.Current.CancellationToken);
        var second = await provider.GetCandlesAsync("EX:AAA", "1h", from, from.AddHours(3), TestContext.Current.CancellationToken);

        first.Candles.ShouldBe(second.Candles);
    }

    [Fact]
    public async Task Candle_Scenarios_Match_Quote_Scenarios()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var outage = await Create("outage").GetCandlesAsync("EX:AAA", "1d", from, from.AddDays(1), TestContext.Current.CancellationToken);
        outage.Outcome.ShouldBe(ProviderOutcome.TransientFailure);

        var limited = await Create("rate_limited").GetCandlesAsync("EX:AAA", "1d", from, from.AddDays(1), TestContext.Current.CancellationToken);
        limited.Outcome.ShouldBe(ProviderOutcome.RateLimited);

        var unknown = await Create("unknown_symbol").GetCandlesAsync("EX:AAA", "1d", from, from.AddDays(1), TestContext.Current.CancellationToken);
        unknown.Outcome.ShouldBe(ProviderOutcome.SymbolNotFound);
    }

    [Fact]
    public async Task Unsupported_Interval_Throws()
    {
        var provider = Create();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            provider.GetCandlesAsync("EX:AAA", "5m", from, from.AddDays(1), TestContext.Current.CancellationToken));
    }
}

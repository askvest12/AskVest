using AskVest.Modules.MarketData.Domain.Enums;
using AskVest.Modules.MarketData.Services;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.MarketData;

public sealed class FreshnessLabelerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> LabelCases()
    {
        // delayMinutes, session, asOfOffsetSeconds, liveThreshold, staleThreshold, expectedLabel, expectedUsable
        yield return [0, "regular", 10, 30, 300, FreshnessLabel.Live, true];
        yield return [0, "regular", 30, 30, 300, FreshnessLabel.Live, true];
        yield return [0, "regular", 31, 30, 300, FreshnessLabel.Delayed, true];
        yield return [0, "regular", 300, 30, 300, FreshnessLabel.Delayed, true];
        yield return [0, "regular", 301, 30, 300, FreshnessLabel.Stale, false];
        yield return [15, "regular", 10, 30, 300, FreshnessLabel.Delayed, true];
        yield return [15, "regular", 1000, 30, 300, FreshnessLabel.Delayed, true];
        yield return [15, "regular", 1200, 30, 300, FreshnessLabel.Delayed, true];
        yield return [15, "regular", 1201, 30, 300, FreshnessLabel.Stale, false];
        yield return [0, "pre", 10, 30, 300, FreshnessLabel.Live, false];
        yield return [0, "post", 10, 30, 300, FreshnessLabel.Live, false];
        yield return [0, "unknown", 10, 30, 300, FreshnessLabel.Live, false];
        yield return [0, "halted", 10, 30, 300, FreshnessLabel.Halted, false];
        yield return [0, "halted", 99999, 30, 300, FreshnessLabel.Halted, false];
        yield return [0, "closed", 10, 30, 300, FreshnessLabel.Closed, false];
        yield return [0, "closed", 99999, 30, 300, FreshnessLabel.Closed, false];
        yield return [0, "regular", -60, 30, 300, FreshnessLabel.Live, true];
    }

    [Theory]
    [MemberData(nameof(LabelCases))]
    public void Label_Matches_Spec_Table(
        int delayMinutes,
        string session,
        int asOfOffsetSeconds,
        int liveThreshold,
        int staleThreshold,
        FreshnessLabel expectedLabel,
        bool expectedUsable)
    {
        var asOf = Now.AddSeconds(-asOfOffsetSeconds);
        var result = FreshnessLabeler.Evaluate(delayMinutes, session, asOf, Now, liveThreshold, staleThreshold);

        result.Label.ShouldBe(expectedLabel);
        result.UsableForExecution.ShouldBe(expectedUsable);
    }

    [Fact]
    public void No_Price_Is_Unavailable()
    {
        var result = FreshnessLabeler.Evaluate(0, "regular", null, Now, 30, 300);
        result.Label.ShouldBe(FreshnessLabel.Unavailable);
        result.UsableForExecution.ShouldBeFalse();
    }

    [Fact]
    public void Provider_Delay_Larger_Than_Age_Yields_Zero_Effective_Age()
    {
        var result = FreshnessLabeler.Evaluate(15, "regular", Now.AddMinutes(-5), Now, 30, 300);
        result.AgeSeconds.ShouldBe(0);
        result.Label.ShouldBe(FreshnessLabel.Delayed);
    }

    [Fact]
    public void Real_Time_Provider_With_Age_Above_Stale_Is_Stale()
    {
        var result = FreshnessLabeler.Evaluate(0, "regular", Now.AddSeconds(-400), Now, 30, 300);
        result.Label.ShouldBe(FreshnessLabel.Stale);
        result.AgeSeconds.ShouldBe(400);
    }

    [Fact]
    public void Unknown_Session_Value_Is_Treated_As_Unknown()
    {
        var result = FreshnessLabeler.Evaluate(0, null, Now.AddSeconds(-10), Now, 30, 300);
        result.Label.ShouldBe(FreshnessLabel.Live);
        result.UsableForExecution.ShouldBeFalse();
    }
}

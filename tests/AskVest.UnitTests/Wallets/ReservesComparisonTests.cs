using AskVest.Modules.Wallets.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Wallets;

public sealed class ReservesComparisonTests
{
    public static TheoryData<string, string, bool> MatchCases => new()
    {
        { "10", "10", true },
        { "9.999999", "10", false },
        { "10.000001", "10", false },
        { "0", "0", true },
        { "-1", "0", false },
    };

    [Theory]
    [MemberData(nameof(MatchCases))]
    public void Holdings_Must_Equal_Ledger_Totals(
        string custody, string ledger, bool expected)
        => ReservesComparison.Matches(decimal.Parse(custody), decimal.Parse(ledger))
            .ShouldBe(expected);

    [Fact]
    public void Consecutive_Mismatches_Accumulate_And_Reset_On_Match()
    {
        var count = ReservesComparison.NextMismatchCount(0, matches: false);
        count.ShouldBe(1);
        count = ReservesComparison.NextMismatchCount(count, matches: false);
        count.ShouldBe(2);
        ReservesComparison.ShouldPause(count, threshold: 2).ShouldBeTrue();
        count = ReservesComparison.NextMismatchCount(count, matches: true);
        count.ShouldBe(0);
        ReservesComparison.ShouldPause(count, threshold: 2).ShouldBeFalse();
    }

    [Fact]
    public void Pause_Requires_The_Configured_Threshold()
    {
        ReservesComparison.ShouldPause(1, 2).ShouldBeFalse();
        ReservesComparison.ShouldPause(2, 2).ShouldBeTrue();
        ReservesComparison.ShouldPause(3, 2).ShouldBeTrue();
    }
}

using AskVest.Modules.Learning.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Learning;

public sealed class StreakTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    [Fact]
    public void No_Days_Gives_Zero_Streak()
    {
        var result = Streak.Compute([], Today);
        result.Current.ShouldBe(0);
        result.Longest.ShouldBe(0);
    }

    [Fact]
    public void Today_Only_Counts()
    {
        var result = Streak.Compute([Today], Today);
        result.Current.ShouldBe(1);
        result.Longest.ShouldBe(1);
    }

    [Fact]
    public void Yesterday_Only_Still_Counts()
    {
        var result = Streak.Compute([Today.AddDays(-1)], Today);
        result.Current.ShouldBe(1);
        result.Longest.ShouldBe(1);
    }

    [Fact]
    public void Gap_Of_Two_Days_Resets_Current()
    {
        var result = Streak.Compute([Today.AddDays(-2), Today.AddDays(-3)], Today);
        result.Current.ShouldBe(0);
        result.Longest.ShouldBe(2);
    }

    [Fact]
    public void Consecutive_Run_Through_Yesterday_Counts()
    {
        var days = Enumerable.Range(1, 4).Select(offset => Today.AddDays(-offset)).ToList();
        var result = Streak.Compute(days, Today);
        result.Current.ShouldBe(4);
        result.Longest.ShouldBe(4);
    }

    [Fact]
    public void Run_Including_Today_Counts()
    {
        var days = Enumerable.Range(0, 3).Select(offset => Today.AddDays(-offset)).ToList();
        var result = Streak.Compute(days, Today);
        result.Current.ShouldBe(3);
        result.Longest.ShouldBe(3);
    }

    [Fact]
    public void Longest_Can_Exceed_Current()
    {
        var days = new List<DateOnly>
        {
            Today.AddDays(-10), Today.AddDays(-9), Today.AddDays(-8),
            Today.AddDays(-1),
        };
        var result = Streak.Compute(days, Today);
        result.Current.ShouldBe(1);
        result.Longest.ShouldBe(3);
    }

    [Fact]
    public void Duplicate_Days_Count_Once()
    {
        var result = Streak.Compute([Today, Today, Today.AddDays(-1)], Today);
        result.Current.ShouldBe(2);
        result.Longest.ShouldBe(2);
    }

    [Fact]
    public void Future_Days_Do_Not_Extend_The_Streak()
    {
        var result = Streak.Compute([Today.AddDays(1), Today.AddDays(2)], Today);
        result.Current.ShouldBe(0);
        result.Longest.ShouldBe(2);
    }
}

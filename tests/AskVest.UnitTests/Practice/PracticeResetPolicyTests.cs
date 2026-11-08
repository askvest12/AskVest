using AskVest.Modules.Practice.Configuration;
using AskVest.Modules.Practice.Services;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Practice;

public sealed class PracticeResetPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PracticeOptions Options(int cooldownHours = 24, int maxResets = 3)
        => new() { ResetCooldownHours = cooldownHours, MaxResetsPer30Days = maxResets };

    [Fact]
    public void Cooldown_Boundary_Exactly_Allows()
    {
        var decision = PracticeResetPolicy.Evaluate(T0, T0.AddHours(24), 0, Options());
        decision.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void One_Second_Before_Cooldown_Ends_Refuses()
    {
        var decision = PracticeResetPolicy.Evaluate(T0, T0.AddHours(24).AddSeconds(-1), 0, Options());
        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldBe("cooldown_not_elapsed");
        decision.NextAllowedAt.ShouldBe(T0.AddHours(24));
    }

    [Fact]
    public void Third_Reset_In_Window_Is_Allowed()
    {
        var decision = PracticeResetPolicy.Evaluate(T0.AddDays(-1), T0, 2, Options());
        decision.Allowed.ShouldBeTrue();
        decision.ResetsLeft.ShouldBe(1);
    }

    [Fact]
    public void Fourth_Reset_In_Window_Is_Refused()
    {
        var decision = PracticeResetPolicy.Evaluate(T0.AddDays(-1), T0, 3, Options());
        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldBe("reset_limit_reached");
        decision.ResetsLeft.ShouldBe(0);
    }

    [Fact]
    public void Window_Rolls_Forward()
    {
        // The caller counts resets inside the rolling 30 day window; once the
        // oldest reset ages out the count drops and resets are allowed again.
        var inside = PracticeResetPolicy.Evaluate(T0.AddDays(-29), T0, 3, Options());
        inside.Allowed.ShouldBeFalse();
        var aged = PracticeResetPolicy.Evaluate(T0.AddDays(-31), T0, 0, Options());
        aged.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Cooldown_Uses_Last_Reset_When_Present()
    {
        // Evaluate receives lastActivityAt already resolved by the caller; a
        // recent last reset pushes the boundary forward.
        var lastReset = T0.AddHours(-2);
        var decision = PracticeResetPolicy.Evaluate(lastReset, T0, 0, Options());
        decision.Allowed.ShouldBeFalse();
        decision.NextAllowedAt.ShouldBe(lastReset.AddHours(24));
    }

    [Fact]
    public void Zero_Cooldown_Allows_Immediately()
    {
        var decision = PracticeResetPolicy.Evaluate(T0, T0, 0, Options(cooldownHours: 0));
        decision.Allowed.ShouldBeTrue();
    }
}

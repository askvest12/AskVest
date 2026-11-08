using AskVest.Modules.MarketData.Domain.Enums;
using AskVest.Modules.MarketData.Services;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.MarketData;

public sealed class OutlierGuardTests
{
    private const decimal Tolerance = 30m;

    [Fact]
    public void Tick_Within_Tolerance_Is_Accepted()
    {
        OutlierGuard.Evaluate(100m, null, 125m, Tolerance).ShouldBe(OutlierDecision.Accepted);
        OutlierGuard.Evaluate(100m, null, 129.99m, Tolerance).ShouldBe(OutlierDecision.Accepted);
    }

    [Fact]
    public void Tick_Outside_Tolerance_Is_Held_As_Suspect()
    {
        OutlierGuard.Evaluate(100m, null, 131m, Tolerance).ShouldBe(OutlierDecision.HeldAsSuspect);
        OutlierGuard.Evaluate(100m, null, 50m, Tolerance).ShouldBe(OutlierDecision.HeldAsSuspect);
    }

    [Fact]
    public void Tick_Near_Suspect_Confirms_It()
    {
        OutlierGuard.Evaluate(100m, 600m, 590m, Tolerance).ShouldBe(OutlierDecision.ConfirmedSuspect);
        OutlierGuard.Evaluate(100m, 600m, 620m, Tolerance).ShouldBe(OutlierDecision.ConfirmedSuspect);
    }

    [Fact]
    public void Tick_Near_Old_Price_Discards_Suspect()
    {
        OutlierGuard.Evaluate(100m, 600m, 105m, Tolerance).ShouldBe(OutlierDecision.DiscardedSuspect);
    }

    [Fact]
    public void Tick_Far_From_Both_Keeps_Suspect_Pending()
    {
        OutlierGuard.Evaluate(100m, 600m, 200m, Tolerance).ShouldBe(OutlierDecision.HeldAsSuspect);
    }

    [Fact]
    public void Exactly_At_Tolerance_Is_Accepted()
    {
        OutlierGuard.Evaluate(100m, null, 130m, Tolerance).ShouldBe(OutlierDecision.Accepted);
    }
}


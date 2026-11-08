using AskVest.Modules.MarketData.Domain.Enums;

namespace AskVest.Modules.MarketData.Services;

internal static class OutlierGuard
{
    public static OutlierDecision Evaluate(
        decimal storedPrice,
        decimal? suspectPrice,
        decimal newPrice,
        decimal maxTickChangePercent)
    {
        var tolerance = storedPrice * maxTickChangePercent / 100m;
        var deviatesFromStored = Math.Abs(newPrice - storedPrice) > tolerance;

        if (!deviatesFromStored)
        {
            return suspectPrice is null ? OutlierDecision.Accepted : OutlierDecision.DiscardedSuspect;
        }

        if (suspectPrice is { } suspect
            && Math.Abs(newPrice - suspect) <= suspect * maxTickChangePercent / 100m)
        {
            return OutlierDecision.ConfirmedSuspect;
        }

        return OutlierDecision.HeldAsSuspect;
    }
}

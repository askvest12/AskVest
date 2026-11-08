namespace AskVest.Modules.MarketData.Domain.Enums;

public enum OutlierDecision
{
    Accepted,
    HeldAsSuspect,
    ConfirmedSuspect,
    DiscardedSuspect,
    IgnoredStale,
}

namespace AskVest.Modules.MarketData.Domain;

internal static class MarketDataVocabulary
{
    public const string SubjectInstrument = "instrument";

    public static readonly HashSet<string> Sessions = new(StringComparer.Ordinal)
        { "pre", "regular", "post", "closed", "halted", "unknown" };

    public static readonly HashSet<string> Scenarios = new(StringComparer.Ordinal)
        { "normal", "delayed", "halted", "closed", "outlier_spike", "outage", "rate_limited", "unknown_symbol", "drop" };

    public static readonly HashSet<string> Intervals = new(StringComparer.Ordinal) { "15m", "1h", "1d" };
}

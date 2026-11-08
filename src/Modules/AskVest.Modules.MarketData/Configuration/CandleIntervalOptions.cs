using Microsoft.Extensions.Options;

namespace AskVest.Modules.MarketData.Configuration;

public sealed class CandleIntervalOptions
{
    public string Interval { get; set; } = "";

    public int MaxRangeDays { get; set; }

    public int MaxCandles { get; set; } = 500;

    public int OpenCacheSeconds { get; set; }
}

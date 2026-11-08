namespace AskVest.Modules.MarketData.Persistence.Entities;

public sealed class Candle
{
    public string SubjectType { get; set; } = "";

    public string SubjectId { get; set; } = "";

    public string Interval { get; set; } = "";

    public DateTimeOffset OpenTime { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public decimal Volume { get; set; }

    public bool Adjusted { get; set; }

    public bool Partial { get; set; }

    public string ProviderKey { get; set; } = "";

    public DateTimeOffset FetchedAt { get; set; }
}

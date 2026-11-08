namespace AskVest.Modules.MarketData.Persistence.Entities;

public sealed class LatestPrice
{
    public string SubjectType { get; set; } = "";

    public string SubjectId { get; set; } = "";

    public decimal Price { get; set; }

    public string Currency { get; set; } = "";

    public DateTimeOffset AsOf { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public string Session { get; set; } = "";

    public decimal? PreviousClose { get; set; }

    public string ProviderKey { get; set; } = "";

    public int DelayMinutes { get; set; }

    public decimal? SuspectPrice { get; set; }

    public DateTimeOffset? SuspectAt { get; set; }

    public DateTimeOffset? UnmappedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

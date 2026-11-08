namespace AskVest.Modules.Portfolio.Persistence.Entities;

public sealed class WatchlistItem
{
    public Guid UserId { get; set; }

    public string InstrumentId { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; }
}

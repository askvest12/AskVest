using AskVest.Modules.Trading.Domain;

namespace AskVest.Modules.Trading.Persistence.Entities;

public sealed class TradingEvent
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid? OrderId { get; set; }

    public TradingEventType EventType { get; set; }

    public string? FromState { get; set; }

    public string? ToState { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string? CorrelationId { get; set; }

    public string? DetailCode { get; set; }
}

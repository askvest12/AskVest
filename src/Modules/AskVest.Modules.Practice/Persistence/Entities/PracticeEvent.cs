using AskVest.Modules.Practice.Domain;

namespace AskVest.Modules.Practice.Persistence.Entities;

public sealed class PracticeEvent
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public PracticeEventType EventType { get; set; }

    public Guid SubjectId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string? CorrelationId { get; set; }
}

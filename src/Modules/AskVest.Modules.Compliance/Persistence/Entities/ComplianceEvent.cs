using AskVest.Modules.Compliance.Domain.Enums;

namespace AskVest.Modules.Compliance.Persistence.Entities;

public sealed class ComplianceEvent
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public ComplianceEventType EventType { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? CorrelationId { get; set; }
    public string? Metadata { get; set; }
}

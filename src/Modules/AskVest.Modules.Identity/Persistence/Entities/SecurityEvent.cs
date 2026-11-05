using AskVest.Modules.Identity.Domain.Enums;

namespace AskVest.Modules.Identity.Persistence.Entities;

public sealed class SecurityEvent
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public SecurityEventType EventType { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public Guid? DeviceId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Metadata { get; set; }
}

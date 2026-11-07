namespace AskVest.Modules.Compliance.Persistence.Entities;

public sealed class KycWebhookEvent
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string BodyHash { get; set; } = string.Empty;
}

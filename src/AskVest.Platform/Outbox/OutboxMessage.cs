namespace AskVest.Platform;

public sealed class OutboxMessage
{
    public const string StatusPending = "pending";
    public const string StatusProcessed = "processed";
    public const string StatusDead = "dead";

    public const string OutcomeHandled = "handled";
    public const string OutcomeNoHandler = "no_handler";

    public Guid Id { get; set; }

    public string MessageType { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset AvailableAt { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    public int Attempts { get; set; }

    public string Status { get; set; } = StatusPending;

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? Outcome { get; set; }

    public string? LastError { get; set; }

    public string? DedupeKey { get; set; }
}

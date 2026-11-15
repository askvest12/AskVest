namespace AskVest.Modules.Funding.Persistence.Entities;

/// <summary>
/// Deduplication record for provider webhooks. Stores the body hash only;
/// the raw payload is never persisted.
/// </summary>
public sealed class FundingWebhookEvent
{
    public Guid Id { get; set; }

    public string ProviderKey { get; set; } = string.Empty;

    public string EventId { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Outcome label: processed, duplicate, ignored, failed.</summary>
    public string Outcome { get; set; } = string.Empty;

    public string BodyHash { get; set; } = string.Empty;
}

namespace AskVest.Modules.Assistant.Persistence.Entities;

/// <summary>
/// Content-free audit record: ids, tool names, source ids, hashes, usage,
/// latency and outcome only. Never message text or raw tool results.
/// Append only: the application role has no update or delete rights.
/// </summary>
public sealed class AuditEvent
{
    public const string OutcomeAnswered = "answered";
    public const string OutcomePolicyBlocked = "policy_blocked";
    public const string OutcomeProviderError = "provider_error";
    public const string OutcomeRateLimited = "rate_limited";
    public const string OutcomeToolRejected = "tool_rejected";
    public const string OutcomeCitationRejected = "citation_rejected";
    public const string OutcomeNumericRegenerated = "numeric_regenerated";

    public Guid Id { get; set; }

    public Guid? UserId { get; set; }

    public Guid? ConversationId { get; set; }

    public Guid? MessageId { get; set; }

    public string ModelId { get; set; } = string.Empty;

    public string PolicyVersion { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string ToolNames { get; set; } = "[]";

    public string CitationSourceIds { get; set; } = "[]";

    public string ToolResultHashes { get; set; } = "[]";

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int LatencyMilliseconds { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;

namespace AskVest.Modules.Assistant.Configuration;

public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    public bool Enabled { get; set; } = true;

    /// <summary>"fake" for development and tests; a real provider key later.</summary>
    public string Provider { get; set; } = "fake";

    /// <summary>Provider model label recorded on audit events.</summary>
    public string Model { get; set; } = "fake-1";

    /// <summary>Version of the system prompt; recorded as PolicyVersion.</summary>
    public string PolicyVersion { get; set; } = "prompt-1";

    [Range(1, 3650)]
    public int ConversationRetentionDays { get; set; } = 90;

    [Range(100, 100_000)]
    public int MaxMessageCharacters { get; set; } = 2_000;

    [Range(1, 100)]
    public int HistoryMessageCount { get; set; } = 20;

    [Range(1, 10_000)]
    public int MaxOutputTokens { get; set; } = 800;

    [Range(1, 100)]
    public int MaxToolCallsPerMessage { get; set; } = 6;

    [Range(1, 50)]
    public int MaxModelCallsPerMessage { get; set; } = 8;

    [Range(1, 60)]
    public int ToolTimeoutSeconds { get; set; } = 5;

    [Range(1, 600)]
    public int MessageTimeBudgetSeconds { get; set; } = 30;

    [Range(1, 10_000)]
    public int MessagesPerHour { get; set; } = 20;

    [Range(1, 100_000)]
    public int MessagesPerDay { get; set; } = 100;

    [Range(1, 500)]
    public int AuditBatchSize { get; set; } = 500;

    public Collection<string> ForbiddenPhrases { get; } = [];

    public string SafeFallbackMessage { get; set; } =
        "I could not put together a reliable answer for that. Please try rephrasing, or browse the lessons for the concepts.";

    public string DisclosureLine { get; set; } =
        "This assistant explains and researches only. It is not financial advice and it cannot place trades.";

    /// <summary>Deterministic fake model scenario; ignored by real providers.</summary>
    public string FakeScenario { get; set; } = "normal";
}

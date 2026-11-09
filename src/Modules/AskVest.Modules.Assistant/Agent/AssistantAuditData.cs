namespace AskVest.Modules.Assistant.Agent;

/// <summary>Content-free audit payload produced by one agent run. The caller attaches user, conversation and message ids before persisting.</summary>
internal sealed record AssistantAuditData(
    string ModelId,
    string PolicyVersion,
    string Outcome,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> CitationSourceIds,
    IReadOnlyList<string> ToolResultHashes,
    int InputTokens,
    int OutputTokens,
    int LatencyMilliseconds);

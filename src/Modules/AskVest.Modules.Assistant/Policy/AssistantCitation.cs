namespace AskVest.Modules.Assistant.Policy;

/// <summary>
/// Trusted display data for one cited source. Built by the server from
/// tool results, never from model text.
/// </summary>
internal sealed record AssistantCitation(
    string Type,
    string Id,
    string Title,
    DateTimeOffset AsOf,
    string? Label);

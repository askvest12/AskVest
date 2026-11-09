namespace AskVest.Modules.Assistant.Contracts;

/// <summary>
/// One model completion: either final <see cref="Text"/> or a list of
/// <see cref="ToolCalls"/> (never both meaningfully; tool calls take
/// precedence when present). Usage feeds audit events.
/// </summary>
public sealed record LanguageModelResponse(
    string? Text,
    IReadOnlyList<LanguageModelToolCall> ToolCalls,
    int InputTokens,
    int OutputTokens,
    string ModelId);

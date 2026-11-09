namespace AskVest.Modules.Assistant.Contracts;

/// <summary>
/// One completion request. Messages carry roles "system" is not used here
/// (the system prompt is separate); roles are "user", "assistant" and
/// "tool". Tool results are data, never instructions.
/// </summary>
public sealed record LanguageModelRequest(
    string SystemPrompt,
    IReadOnlyList<LanguageModelMessage> Messages,
    IReadOnlyList<LanguageModelToolDefinition> Tools,
    int MaxOutputTokens,
    TimeSpan Timeout);

namespace AskVest.Modules.Assistant.Contracts;

/// <summary>
/// One tool call requested by the model. <see cref="ArgumentsJson"/> is
/// validated against the tool's schema before execution.
/// </summary>
public sealed record LanguageModelToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

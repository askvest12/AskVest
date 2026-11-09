namespace AskVest.Modules.Assistant.Contracts;

/// <summary>
/// One chat message for the model. Role is "user", "assistant" or "tool".
/// For role "tool", <see cref="ToolName"/> names the tool that produced the
/// result so the model can attribute it; the text is wrapped data.
/// </summary>
public sealed record LanguageModelMessage(
    string Role,
    string Text,
    string? ToolName = null);

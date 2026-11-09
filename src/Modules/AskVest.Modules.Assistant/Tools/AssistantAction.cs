namespace AskVest.Modules.Assistant.Tools;

/// <summary>
/// A client-facing action proposed by a tool. It describes what a screen
/// would show; it never causes a side effect by itself.
/// </summary>
internal sealed record AssistantAction(string Type, string PayloadJson);

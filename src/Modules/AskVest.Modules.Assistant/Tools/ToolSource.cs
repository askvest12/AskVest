namespace AskVest.Modules.Assistant.Tools;

/// <summary>
/// One verifiable source behind a tool result, used for citation metadata.
/// Never carries personal data beyond the caller's own resources.
/// </summary>
internal sealed record ToolSource(
    string Type,
    string Id,
    string Title,
    DateTimeOffset AsOf,
    string? Label = null);

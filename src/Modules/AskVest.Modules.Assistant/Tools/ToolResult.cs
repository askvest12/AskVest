using System.Text.Json;

namespace AskVest.Modules.Assistant.Tools;

/// <summary>
/// One executed tool call. Successful results carry a result id the model
/// may cite; error results carry a stable code and are returned to the
/// model as errors, never as citable data. The dispatcher fills ResultId,
/// ToolName and AsOf when it wraps the tool's output.
/// </summary>
internal sealed record ToolResult
{
    public string? ResultId { get; init; }

    public required string ToolName { get; init; }

    public DateTimeOffset AsOf { get; init; }

    public bool IsError { get; init; }

    /// <summary>True when the call itself was refused (bad arguments, unknown tool, timeout, over budget), as opposed to a domain miss like not_found.</summary>
    public bool Rejected { get; init; }

    public string? ErrorCode { get; init; }

    public required string PayloadJson { get; init; }

    public IReadOnlyList<ToolSource> Sources { get; init; } = [];

    public AssistantAction? Action { get; init; }

    public JsonElement Payload => JsonDocument.Parse(PayloadJson).RootElement;

    public static ToolResult Ok(string payloadJson, IReadOnlyList<ToolSource> sources, AssistantAction? action = null)
        => new() { ToolName = string.Empty, PayloadJson = payloadJson, Sources = sources, Action = action };

    public static ToolResult Fail(string errorCode, string message)
        => new()
        {
            ToolName = string.Empty,
            IsError = true,
            Rejected = errorCode is "invalid_arguments" or "unknown_tool"
                or ToolArguments.ForbiddenUserIdProperty or "tool_timeout",
            ErrorCode = errorCode,
            PayloadJson = JsonSerializer.Serialize(new { error = message }),
        };
}

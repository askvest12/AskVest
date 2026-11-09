using System.Text.Json;
using AskVest.Modules.Assistant.Configuration;
using AskVest.Modules.Assistant.Contracts;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Assistant.Tools;

/// <summary>
/// Registry and dispatcher for the read-only tool set. It rejects unknown
/// tools, malformed arguments, user-id properties and calls that exceed
/// the per-tool timeout; every outcome is returned to the model.
/// </summary>
internal sealed class AssistantToolSet(IEnumerable<AssistantTool> tools, IOptions<AssistantOptions> options)
{
    private readonly IReadOnlyDictionary<string, AssistantTool> _byName = tools
        .ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    private readonly Dictionary<string, string[]> _allowedArguments = tools
        .ToDictionary(
            tool => tool.Name,
            tool => AllowedProperties(tool),
            StringComparer.Ordinal);

    public IReadOnlyList<LanguageModelToolDefinition> Definitions
        => _byName.Values.Select(tool => tool.Definition).ToList();

    public async Task<ToolResult> ExecuteAsync(
        LanguageModelToolCall call,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var toolName = call.Name;
        ToolResult Finish(ToolResult result) => result with
        {
            ResultId = result.IsError ? null : context.NextResultId(),
            ToolName = toolName,
            AsOf = context.TimeProvider.GetUtcNow(),
        };

        if (!_byName.TryGetValue(toolName, out var tool))
        {
            return Finish(ToolResult.Fail("unknown_tool", $"No tool named '{toolName}' exists."));
        }

        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Finish(ToolResult.Fail("invalid_arguments", "The tool arguments are not valid JSON."));
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Finish(ToolResult.Fail("invalid_arguments", "The tool arguments must be a JSON object."));
        }

        if (ToolArguments.ContainsUserIdProperty(arguments))
        {
            return Finish(ToolResult.Fail(
                ToolArguments.ForbiddenUserIdProperty,
                "Tool arguments must never include a user id; the server scopes every call to the caller."));
        }

        var unknown = ToolArguments.UnknownProperty(arguments, _allowedArguments[toolName]);
        if (unknown is not null)
        {
            return Finish(ToolResult.Fail("invalid_arguments", $"Unknown argument '{unknown}'."));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ToolTimeoutSeconds));
        try
        {
            return Finish(await tool.ExecuteAsync(arguments, context, timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Finish(ToolResult.Fail("tool_timeout", $"The tool '{toolName}' did not answer in time."));
        }
    }

    private static string[] AllowedProperties(AssistantTool tool)
    {
        using var document = JsonDocument.Parse(tool.ParametersSchemaJson);
        return document.RootElement.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
    }
}

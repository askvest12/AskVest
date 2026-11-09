using System.Text.Json;
using AskVest.Modules.Assistant.Contracts;

namespace AskVest.Modules.Assistant.Tools;

/// <summary>
/// Base class for the nine read-only assistant tools. Tools validate their
/// arguments strictly, never accept a user id, and return wrapped data:
/// every payload is data for the model, never instructions.
/// </summary>
internal abstract class AssistantTool
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string ParametersSchemaJson { get; }

    public LanguageModelToolDefinition Definition
        => new(Name, Description, ParametersSchemaJson);

    public abstract Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken);
}

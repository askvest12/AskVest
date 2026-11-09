namespace AskVest.Modules.Assistant.Contracts;

/// <summary>
/// A fixed, server-side tool definition offered to the model. The model
/// cannot invent tools; unknown names are rejected by the agent loop.
/// <see cref="ParametersSchemaJson"/> is a JSON Schema object that rejects
/// unknown properties.
/// </summary>
public sealed record LanguageModelToolDefinition(
    string Name,
    string Description,
    string ParametersSchemaJson);

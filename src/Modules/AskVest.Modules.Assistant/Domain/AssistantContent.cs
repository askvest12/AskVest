using AskVest.Modules.Assistant.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Assistant.Domain;

/// <summary>
/// The validated system prompt, its version and the evaluation set, loaded
/// once. The startup check has already proven the files are valid before
/// this is first resolved.
/// </summary>
internal sealed class AssistantContent
{
    public AssistantContent(IOptions<AssistantOptions> options)
    {
        var settings = options.Value;
        var (promptText, version) = AssistantContentLoader.LoadPrompt(
            Path.Combine(AppContext.BaseDirectory, settings.PromptPath),
            settings.PolicyVersion);
        PromptText = promptText;
        Version = version;
        EvaluationCases = AssistantContentLoader.LoadEvaluationSet(
            Path.Combine(AppContext.BaseDirectory, settings.EvaluationSetPath));
    }

    public string PromptText { get; }

    public string Version { get; }

    public IReadOnlyList<AssistantContentLoader.EvaluationCase> EvaluationCases { get; }
}

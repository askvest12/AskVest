using System.Text.Json;
using System.Text.RegularExpressions;

namespace AskVest.Modules.Assistant.Domain;

/// <summary>
/// Loads and validates the versioned system prompt and the evaluation set
/// at startup. The prompt's first line must be "version: &lt;id&gt;" and must
/// equal the configured PolicyVersion so audits always name the prompt that
/// produced an answer.
/// </summary>
internal static partial class AssistantContentLoader
{
    public const string PromptVersionPrefix = "version:";

    public static (string Text, string Version) LoadPrompt(string path, string expectedVersion)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Assistant prompt file '{path}' does not exist.");
        }

        var text = File.ReadAllText(path);
        var firstLine = text.Split('\n', 2)[0].Trim();
        var match = VersionLine().Match(firstLine);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Assistant prompt file '{path}' must start with a 'version: <id>' line.");
        }

        var version = match.Groups[1].Value;
        if (!string.Equals(version, expectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Assistant prompt version '{version}' does not match Assistant:PolicyVersion '{expectedVersion}'.");
        }

        var body = text[(firstLine.Length + 1)..].Trim();
        if (body.Length == 0)
        {
            throw new InvalidOperationException($"Assistant prompt file '{path}' is empty.");
        }

        return (body, version);
    }

    public static IReadOnlyList<EvaluationCase> LoadEvaluationSet(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Assistant evaluation set '{path}' does not exist.");
        }

        var json = File.ReadAllText(path);
        EvaluationSetFile? file;
        try
        {
            file = JsonSerializer.Deserialize<EvaluationSetFile>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Assistant evaluation set '{path}' is not valid JSON: {exception.Message}");
        }

        if (file?.Cases is null || file.Cases.Count == 0)
        {
            throw new InvalidOperationException($"Assistant evaluation set '{path}' has no cases.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evaluationCase in file.Cases)
        {
            if (string.IsNullOrWhiteSpace(evaluationCase.Id) || !ids.Add(evaluationCase.Id))
            {
                throw new InvalidOperationException($"Assistant evaluation set '{path}' has a missing or duplicate case id.");
            }

            if (string.IsNullOrWhiteSpace(evaluationCase.Message))
            {
                throw new InvalidOperationException($"Assistant evaluation case '{evaluationCase.Id}' has no message.");
            }

            if (evaluationCase.Checks is null || evaluationCase.Checks.Count == 0)
            {
                throw new InvalidOperationException($"Assistant evaluation case '{evaluationCase.Id}' has no checks.");
            }
        }

        return file.Cases;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^version:\\s*(\\S+)\\s*$")]
    private static partial Regex VersionLine();

    private sealed record EvaluationSetFile(int Version, List<EvaluationCase>? Cases);

    public sealed record EvaluationCase(
        string Id,
        string Message,
        string? Scenario,
        IReadOnlyList<string> Checks);
}

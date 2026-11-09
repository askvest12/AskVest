using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Assistant.Configuration;

internal sealed class AssistantOptionsValidator : IValidateOptions<AssistantOptions>
{
    public static readonly AssistantOptionsValidator Instance = new();

    private static readonly HashSet<string> Scenarios = new(StringComparer.Ordinal)
    {
        "normal",
        "injection_obey",
        "fabricated_citation",
        "ungrounded_number",
        "advice_phrase",
        "markup_output",
        "malformed_output",
        "loop_tools",
        "slow",
        "error",
    };

    public ValidateOptionsResult Validate(string? name, AssistantOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        if (!Scenarios.Contains(options.FakeScenario))
        {
            results.Add(new ValidationResult(
                $"Assistant:FakeScenario '{options.FakeScenario}' is not supported."));
        }

        if (string.IsNullOrWhiteSpace(options.SafeFallbackMessage)
            || string.IsNullOrWhiteSpace(options.DisclosureLine)
            || string.IsNullOrWhiteSpace(options.PolicyVersion))
        {
            results.Add(new ValidationResult("Assistant requires PolicyVersion, SafeFallbackMessage and DisclosureLine."));
        }

        return results.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(results.Select(result => result.ErrorMessage ?? "Invalid assistant option."));
    }
}

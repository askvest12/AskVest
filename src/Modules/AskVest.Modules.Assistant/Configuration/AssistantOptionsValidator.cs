using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Assistant.Configuration;

internal sealed class AssistantOptionsValidator : IValidateOptions<AssistantOptions>
{
    public static readonly AssistantOptionsValidator Instance = new();

    public ValidateOptionsResult Validate(string? name, AssistantOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        if (!Domain.AssistantScenarios.All.Contains(options.FakeScenario))
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

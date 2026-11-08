using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Learning.Configuration;

internal sealed class LearningOptionsValidator : IValidateOptions<LearningOptions>
{
    public static readonly LearningOptionsValidator Instance = new();

    public ValidateOptionsResult Validate(string? name, LearningOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(results.Select(result => result.ErrorMessage ?? "Invalid learning option."));
    }
}

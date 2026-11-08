using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Portfolio.Configuration;

internal sealed class PortfolioOptionsValidator : IValidateOptions<PortfolioOptions>
{
    public static readonly PortfolioOptionsValidator Instance = new();

    public ValidateOptionsResult Validate(string? name, PortfolioOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        var equivalents = options.CashEquivalentAssets
            .Select(asset => asset.Trim().ToUpperInvariant())
            .Where(asset => asset.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (equivalents.Count == 0)
        {
            results.Add(new ValidationResult("Portfolio:CashEquivalentAssets must contain at least one asset code."));
        }
        else if (equivalents.Any(asset => asset.Length > 16))
        {
            results.Add(new ValidationResult("Portfolio:CashEquivalentAssets entries must be at most 16 characters."));
        }

        return results.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(results.Select(result => result.ErrorMessage ?? "Invalid portfolio option."));
    }
}

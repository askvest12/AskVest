using System.Text.RegularExpressions;
using AskVest.SharedKernel;
using PhoneNumbers;

namespace AskVest.Modules.Identity.Crypto;

public static partial class PhoneNormalizer
{
    private static readonly Error _invalidPhone = new(
        "validation.failed",
        "The phone number is invalid.",
        SharedKernel.ErrorType.Validation);

    private static readonly PhoneNumberUtil _util = PhoneNumberUtil.GetInstance();

    public static Result<string> Normalize(string? raw, string? defaultRegion = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result.Failure<string>(_invalidPhone);
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > 32 || ControlPattern().IsMatch(trimmed))
        {
            return Result.Failure<string>(_invalidPhone);
        }

        var region = string.IsNullOrWhiteSpace(defaultRegion) ? null : defaultRegion.Trim().ToUpperInvariant();
        PhoneNumber number;
        try
        {
            number = _util.Parse(trimmed, region);
        }
        catch (NumberParseException)
        {
            return Result.Failure<string>(_invalidPhone);
        }

        if (!_util.IsValidNumber(number))
        {
            return Result.Failure<string>(_invalidPhone);
        }

        return Result.Success(_util.Format(number, PhoneNumberFormat.E164));
    }

    [GeneratedRegex("[\\p{C}]")]
    private static partial Regex ControlPattern();
}

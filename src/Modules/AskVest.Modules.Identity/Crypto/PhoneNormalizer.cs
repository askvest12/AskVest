using System.Text.RegularExpressions;
using AskVest.SharedKernel;
using PhoneNumbers;

namespace AskVest.Modules.Identity.Crypto;

public static partial class PhoneNormalizer
{
    private static readonly Error InvalidPhone = new(
        "validation.failed",
        "The phone number is invalid.",
        SharedKernel.ErrorType.Validation);

    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    public static Result<string> Normalize(string? raw, string? defaultRegion = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result.Failure<string>(InvalidPhone);
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > 32 || ControlPattern().IsMatch(trimmed))
        {
            return Result.Failure<string>(InvalidPhone);
        }

        var region = string.IsNullOrWhiteSpace(defaultRegion) ? null : defaultRegion.Trim().ToUpperInvariant();
        PhoneNumber number;
        try
        {
            number = Util.Parse(trimmed, region);
        }
        catch (NumberParseException)
        {
            return Result.Failure<string>(InvalidPhone);
        }

        if (!Util.IsValidNumber(number))
        {
            return Result.Failure<string>(InvalidPhone);
        }

        return Result.Success(Util.Format(number, PhoneNumberFormat.E164));
    }

    [GeneratedRegex("[\\p{C}]")]
    private static partial Regex ControlPattern();
}

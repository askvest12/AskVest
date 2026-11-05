using System.Text;
using System.Text.RegularExpressions;
using AskVest.SharedKernel;

namespace AskVest.Modules.Identity.Crypto;

public static partial class EmailNormalizer
{
    private static readonly Error InvalidEmail = new(
        "validation.failed",
        "The email address is invalid.",
        SharedKernel.ErrorType.Validation);

    public static Result<string> Normalize(string? raw, int maxLength = 254)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result.Failure<string>(InvalidEmail);
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > maxLength || trimmed.Length < 3)
        {
            return Result.Failure<string>(InvalidEmail);
        }

        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                return Result.Failure<string>(InvalidEmail);
            }
        }

        var parts = trimmed.Split('@');
        if (parts.Length != 2)
        {
            return Result.Failure<string>(InvalidEmail);
        }

        var local = parts[0];
        var domain = parts[1];
        if (local.Length is < 1 or > 64 || !LocalPartPattern().IsMatch(local))
        {
            return Result.Failure<string>(InvalidEmail);
        }

        if (!IsValidDomain(domain))
        {
            return Result.Failure<string>(InvalidEmail);
        }

        return Result.Success(trimmed.ToLowerInvariant());
    }

    private static bool IsValidDomain(string domain)
    {
        if (domain.Length is < 3 or > 253 || !domain.Contains('.'))
        {
            return false;
        }

        var labels = domain.Split('.');
        if (labels.Length < 2 || labels[^1].Length < 2)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63
                || label.StartsWith('-')
                || label.EndsWith('-')
                || !LabelPattern().IsMatch(label))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+/=?^_`{|}~.-]+$")]
    private static partial Regex LocalPartPattern();

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex LabelPattern();
}

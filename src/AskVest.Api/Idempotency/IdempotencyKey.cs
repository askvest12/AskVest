using System.Text.RegularExpressions;

namespace AskVest.Api;

internal static partial class IdempotencyKey
{
    public const string HeaderName = "Idempotency-Key";

    internal static bool IsValid(string? key)
        => key is not null && KeyPattern().IsMatch(key);

    [GeneratedRegex("^[A-Za-z0-9_.:-]{8,128}$")]
    private static partial Regex KeyPattern();
}

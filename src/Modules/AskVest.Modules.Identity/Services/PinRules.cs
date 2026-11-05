namespace AskVest.Modules.Identity.Services;

internal static class PinRules
{
    public static bool IsWellFormed(string? pin)
        => pin is { Length: 6 } && pin.All(char.IsAsciiDigit);

    public static bool IsGuessable(string pin)
    {
        if (pin.Distinct().Count() == 1)
        {
            return true;
        }

        var digits = pin.Select(c => c - '0').ToArray();

        var ascending = true;
        var descending = true;
        for (var i = 0; i < digits.Length - 1; i++)
        {
            ascending &= digits[i + 1] == digits[i] + 1;
            descending &= digits[i + 1] == digits[i] - 1;
        }

        if (ascending || descending)
        {
            return true;
        }

        var repeatedPairs = digits[0] == digits[1] && digits[2] == digits[3] && digits[4] == digits[5];
        var alternating = digits[0] == digits[2] && digits[2] == digits[4]
            && digits[1] == digits[3] && digits[3] == digits[5];
        var halves = digits[0] == digits[3] && digits[1] == digits[4] && digits[2] == digits[5];
        return repeatedPairs || alternating || halves;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace AskVest.Modules.Assistant.Policy;

/// <summary>
/// Numeric grounding floor: every figure in the answer that carries a
/// currency symbol, a percent sign, a decimal point or a value of 100 or
/// more must appear in this message's tool payloads or in the user's own
/// message, allowing display rounding. Small plain integers and numbers
/// embedded in identifiers pass unchecked.
/// </summary>
internal static partial class NumericGrounding
{
    public static bool IsGrounded(string answer, string corpus)
    {
        foreach (Match match in FigureRegex().Matches(answer))
        {
            var figure = match.Groups["num"];
            var hasCurrency = match.Groups["cur"].Success;
            var hasPercent = match.Groups["pct"].Success;
            var text = figure.Value.Replace(",", string.Empty, StringComparison.Ordinal);
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            var needsCheck = hasCurrency || hasPercent || text.Contains('.', StringComparison.Ordinal)
                || Math.Abs(value) >= 100m;
            if (needsCheck && !CorpusContains(corpus, value, DecimalsIn(text)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CorpusContains(string corpus, decimal figure, int decimals)
    {
        var unit = 1m;
        for (var index = 0; index < decimals; index++)
        {
            unit /= 10m;
        }

        var tolerance = unit / 2m * 1.0000001m;
        foreach (Match match in CorpusNumberRegex().Matches(corpus))
        {
            var text = match.Value.Replace(",", string.Empty, StringComparison.Ordinal);
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var candidate)
                && Math.Abs(candidate - figure) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static int DecimalsIn(string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? 0 : text.Length - dot - 1;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])(?<cur>[$€£])?\s*(?<num>\d[\d,]*(?:\.\d+)?)\s*(?<pct>%)?")]
    private static partial Regex FigureRegex();

    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?")]
    private static partial Regex CorpusNumberRegex();
}

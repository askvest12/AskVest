using System.Text.RegularExpressions;

namespace AskVest.Modules.Assistant.Policy;

/// <summary>
/// Deterministic floor under model output. Blocks direct personal
/// recommendations, return promises, price predictions stated as fact and
/// certainty claims. Configured phrases combine with structural patterns;
/// a hit is a floor, not a guarantee, which is why the evaluation set
/// exists. Returns the violation category or null.
/// </summary>
internal static partial class AnswerPolicy
{
    public const string CategoryRecommendation = "recommendation";
    public const string CategoryPromise = "promise";
    public const string CategoryPrediction = "prediction";
    public const string CategoryCertainty = "certainty";

    public static string? Check(string text, IReadOnlyList<string> forbiddenPhrases)
    {
        foreach (var phrase in forbiddenPhrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return CategoryForPhrase(phrase) ?? CategoryRecommendation;
            }
        }

        if (RecommendationRegex().IsMatch(text))
        {
            return CategoryRecommendation;
        }

        if (PromiseRegex().IsMatch(text))
        {
            return CategoryPromise;
        }

        if (PredictionRegex().IsMatch(text))
        {
            return CategoryPrediction;
        }

        if (CertaintyRegex().IsMatch(text))
        {
            return CategoryCertainty;
        }

        return null;
    }

    private static string? CategoryForPhrase(string phrase)
    {
        if (phrase.Contains("recommend", StringComparison.OrdinalIgnoreCase)
            || phrase.Contains("should", StringComparison.OrdinalIgnoreCase)
            || phrase.Contains("advise", StringComparison.OrdinalIgnoreCase))
        {
            return CategoryRecommendation;
        }

        if (phrase.Contains("guarantee", StringComparison.OrdinalIgnoreCase)
            || phrase.Contains("promise", StringComparison.OrdinalIgnoreCase)
            || phrase.Contains("sure", StringComparison.OrdinalIgnoreCase)
            || phrase.Contains("risk", StringComparison.OrdinalIgnoreCase))
        {
            return CategoryPromise;
        }

        if (phrase.Contains("will", StringComparison.OrdinalIgnoreCase)
            || phrase.Contains("predict", StringComparison.OrdinalIgnoreCase)
            || phrase.Contains("forecast", StringComparison.OrdinalIgnoreCase)
            || phrase.Contains("target", StringComparison.OrdinalIgnoreCase))
        {
            return CategoryPrediction;
        }

        if (phrase.Contains("certain", StringComparison.OrdinalIgnoreCase)
            || phrase.Contains("definite", StringComparison.OrdinalIgnoreCase)
            || phrase.Contains("doubt", StringComparison.OrdinalIgnoreCase))
        {
            return CategoryCertainty;
        }

        return null;
    }

    [GeneratedRegex(@"\b(you should|you shouldn't|you ought to|you must|you need to|i recommend|we recommend|i advise|i suggest you)\b[^.]{0,60}\b(buy|sell|invest|purchase|hold|avoid|trade|put money)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RecommendationRegex();

    [GeneratedRegex(@"\b(sure thing|cannot lose|can't lose|risk[- ]free|guaranteed\s+(returns?|profits?|gains?|income)|promised\s+(returns?|profits?|gains?))\b|\b(guarantee[ds]?|guaranteeing|promises?|assured|locked[- ]in)\b[^.]{0,60}\b(returns?|profits?|gains?|rise|fall|price|earn|make money|income)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PromiseRegex();

    [GeneratedRegex(@"\b(will|is going to|going to|expects?|predicts?|forecasts?)\b[^.]{0,60}\b(rise|fall|go up|go down|soar|plunge|crash|surge|hit|reach|double|halve|new high|new low|price target)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PredictionRegex();

    [GeneratedRegex(@"\b(certainly|definitely|undoubtedly|without (a )?doubt|bound to|sure to|cannot fail|can't fail)\b|100\s*%", RegexOptions.IgnoreCase)]
    private static partial Regex CertaintyRegex();
}

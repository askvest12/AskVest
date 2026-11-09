using System.Text.RegularExpressions;

namespace AskVest.Modules.Assistant.Policy;

/// <summary>
/// Reduces model text to plain display text: HTML tags, markdown links,
/// bare URLs and control characters are removed and whitespace collapses.
/// Clients render the result as plain text only.
/// </summary>
internal static partial class TextSanitizer
{
    public static string Sanitize(string text)
    {
        var withoutImages = MarkdownImageRegex().Replace(text, " ");
        var withoutLinks = MarkdownLinkRegex().Replace(withoutImages, "$1");
        var withoutUrls = UrlRegex().Replace(withoutLinks, " ");
        var withoutTags = HtmlTagRegex().Replace(withoutUrls, " ");
        var withoutControl = ControlCharsRegex().Replace(withoutTags, " ");
        return WhitespaceRegex().Replace(withoutControl, " ").Trim();
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"(?:https?://|www\.)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex ControlCharsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

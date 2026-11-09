using System.Text.RegularExpressions;

namespace AskVest.Modules.Assistant.Policy;

/// <summary>
/// Citation marker handling. Markers are "[rN]" where N is a result id
/// issued during this message only. Unknown markers are stripped from the
/// text and the rejection is reported so the audit event records it.
/// </summary>
internal static partial class CitationValidator
{
    public static (string Text, IReadOnlyList<AssistantCitation> Citations, bool Rejected) Validate(
        string text,
        IReadOnlyList<Tools.ToolResult> results)
    {
        var byId = results
            .Where(result => result.ResultId is not null && !result.IsError)
            .GroupBy(result => result.ResultId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var cited = new List<string>();
        var rejected = false;

        var cleaned = MarkerRegex().Replace(text, match =>
        {
            var id = "r" + match.Groups[1].Value;
            if (!byId.TryGetValue(id, out var result))
            {
                rejected = true;
                return " ";
            }

            // A result with no sources has nothing to cite, so the marker is
            // dropped quietly rather than reported as a fabricated citation.
            if (result.Sources.Count == 0)
            {
                return " ";
            }

            if (!cited.Contains(id, StringComparer.Ordinal))
            {
                cited.Add(id);
            }

            return match.Value;
        });

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var citations = new List<AssistantCitation>();
        foreach (var id in cited)
        {
            foreach (var source in byId[id].Sources)
            {
                var key = $"{source.Type}:{source.Id}";
                if (seen.Add(key))
                {
                    citations.Add(new AssistantCitation(
                        source.Type, source.Id, source.Title, source.AsOf, source.Label));
                }
            }
        }

        return (WhitespaceRegex().Replace(cleaned, " ").Trim(), citations, rejected);
    }

    [GeneratedRegex(@"\[r(\d+)\]")]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

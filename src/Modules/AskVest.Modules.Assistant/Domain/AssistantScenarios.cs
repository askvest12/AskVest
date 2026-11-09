namespace AskVest.Modules.Assistant.Domain;

/// <summary>
/// The deterministic scenarios the fake model understands. Shared by the
/// options validator and the diagnostics probe so both agree on the set.
/// </summary>
internal static class AssistantScenarios
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "normal",
        "injection_obey",
        "fabricated_citation",
        "ungrounded_number",
        "advice_phrase",
        "markup_output",
        "malformed_output",
        "loop_tools",
        "slow",
        "error",
    };
}

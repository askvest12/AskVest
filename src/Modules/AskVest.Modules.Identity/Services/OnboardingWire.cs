using AskVest.Modules.Identity.Domain.Enums;

namespace AskVest.Modules.Identity.Services;

/// <summary>
/// Maps onboarding enums to the stable wire strings the client sends and
/// receives. Stored values stay enum names; wire values are lowercase.
/// </summary>
internal static class OnboardingWire
{
    private static readonly Dictionary<string, OnboardingFamiliarity> _familiarityByWire =
        new Dictionary<string, OnboardingFamiliarity>(StringComparer.Ordinal)
        {
            ["newcomer"] = OnboardingFamiliarity.Newcomer,
            ["some"] = OnboardingFamiliarity.Some,
            ["experienced"] = OnboardingFamiliarity.Experienced,
        };

    private static readonly Dictionary<OnboardingFamiliarity, string> _familiarityWire =
        new Dictionary<OnboardingFamiliarity, string>
        {
            [OnboardingFamiliarity.Newcomer] = "newcomer",
            [OnboardingFamiliarity.Some] = "some",
            [OnboardingFamiliarity.Experienced] = "experienced",
        };

    private static readonly (string Wire, OnboardingGoal Goal)[] _goalPairs =
    [
        ("learn_investing", OnboardingGoal.LearnInvesting),
        ("build_wealth", OnboardingGoal.BuildWealth),
        ("discover_opportunities", OnboardingGoal.DiscoverOpportunities),
        ("research_companies", OnboardingGoal.ResearchCompanies),
        ("diversify", OnboardingGoal.Diversify),
        ("practice_first", OnboardingGoal.PracticeFirst),
    ];

    private static readonly Dictionary<string, OnboardingGoal> _goalByWire =
        _goalPairs.ToDictionary(pair => pair.Wire, pair => pair.Goal, StringComparer.Ordinal);

    public static bool TryParseFamiliarity(string? wire, out OnboardingFamiliarity familiarity)
    {
        if (wire is not null && _familiarityByWire.TryGetValue(wire, out var parsed))
        {
            familiarity = parsed;
            return true;
        }

        familiarity = default;
        return false;
    }

    public static string FamiliarityToWire(OnboardingFamiliarity familiarity) => _familiarityWire[familiarity];

    public static bool IsKnownGoal(string wire) => _goalByWire.ContainsKey(wire);

    public static long GoalsToFlags(IEnumerable<string> wires)
    {
        long flags = 0;
        foreach (var wire in wires)
        {
            if (_goalByWire.TryGetValue(wire, out var goal))
            {
                flags |= (long)goal;
            }
        }

        return flags;
    }

    public static IReadOnlyList<string> FlagsToGoals(long flags)
        => _goalPairs
            .Where(pair => (flags & (long)pair.Goal) == (long)pair.Goal)
            .Select(pair => pair.Wire)
            .ToList();
}

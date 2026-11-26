namespace AskVest.Modules.Identity.Domain.Enums;

[Flags]
public enum OnboardingGoal
{
    None = 0,
    LearnInvesting = 1,
    BuildWealth = 2,
    DiscoverOpportunities = 4,
    ResearchCompanies = 8,
    Diversify = 16,
    PracticeFirst = 32,
}

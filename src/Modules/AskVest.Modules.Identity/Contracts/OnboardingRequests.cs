namespace AskVest.Modules.Identity.Contracts;

public sealed record OnboardingRequest(
    string? FullName,
    string? Familiarity,
    IReadOnlyList<string>? Goals,
    string? PreferredCurrency);

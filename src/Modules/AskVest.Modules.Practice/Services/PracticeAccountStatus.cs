namespace AskVest.Modules.Practice.Services;

internal sealed record PracticeAccountStatus(
    bool Started,
    decimal Cash,
    int ResetCount,
    int ResetsLeft,
    DateTimeOffset? NextResetAt);

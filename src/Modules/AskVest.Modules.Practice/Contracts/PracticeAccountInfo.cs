namespace AskVest.Modules.Practice.Contracts;

public sealed record PracticeAccountInfo(
    bool Started,
    DateTimeOffset? StartedAt,
    int ResetCount,
    DateTimeOffset? LastResetAt);

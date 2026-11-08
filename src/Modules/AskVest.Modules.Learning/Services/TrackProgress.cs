namespace AskVest.Modules.Learning.Services;

public sealed record TrackProgress(
    string Id,
    string Title,
    int TotalLessons,
    int CompletedLessons,
    int AttemptedLessons);


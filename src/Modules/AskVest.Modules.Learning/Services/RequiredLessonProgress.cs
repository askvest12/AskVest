namespace AskVest.Modules.Learning.Services;

public sealed record RequiredLessonProgress(
    string Id,
    string? TrackId,
    int Version,
    string Status,
    int? CompletedVersion);

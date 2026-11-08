namespace AskVest.Modules.Learning.Contracts;

/// <summary>
/// A lesson flagged <c>requiredForReal</c> that the user has not completed at
/// its current content version.
/// </summary>
public sealed record RequiredLessonRef(string LessonId, int Version);

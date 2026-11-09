namespace AskVest.Modules.Learning.Contracts;

/// <summary>
/// The only surface other modules may use. Compliance reads incomplete
/// required lessons here; nothing else crosses the boundary.
/// </summary>
public interface ILearningRequirements
{
    Task<IReadOnlyList<RequiredLessonRef>> GetIncompleteRequiredLessonsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Case-insensitive substring search over lesson ids, titles, summaries
    /// and body text from the in-memory snapshot. Read-only; never exposes
    /// quiz answers or explanations.
    /// </summary>
    Task<IReadOnlyList<LessonSearchHit>> SearchLessonsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default);
}

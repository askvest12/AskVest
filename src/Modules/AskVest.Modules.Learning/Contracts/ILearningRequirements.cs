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
}

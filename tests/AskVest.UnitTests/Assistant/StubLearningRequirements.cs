using AskVest.Modules.Learning.Contracts;

namespace AskVest.UnitTests.Assistant;

internal sealed class StubLearningRequirements : ILearningRequirements
{
    public List<LessonSearchHit> Hits { get; } = [];

    public Task<IReadOnlyList<RequiredLessonRef>> GetIncompleteRequiredLessonsAsync(
        Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RequiredLessonRef>>([]);

    public Task<IReadOnlyList<LessonSearchHit>> SearchLessonsAsync(
        string query, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LessonSearchHit>>(
            Hits.Where(hit => hit.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || hit.LessonId.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList());
}

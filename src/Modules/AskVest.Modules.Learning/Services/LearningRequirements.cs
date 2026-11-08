using AskVest.Modules.Learning.Content;
using AskVest.Modules.Learning.Contracts;
using AskVest.Modules.Learning.Persistence;
using AskVest.Modules.Learning.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Learning.Services;

internal sealed class LearningRequirements(
    LearningDbContext db,
    LearningContentSnapshot snapshot) : ILearningRequirements
{
    public async Task<IReadOnlyList<RequiredLessonRef>> GetIncompleteRequiredLessonsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var required = snapshot.RequiredLessons().ToList();
        if (required.Count == 0)
        {
            return [];
        }

        var requiredIds = required.Select(lesson => lesson.Id).ToList();
        var rows = await db.LessonProgress
            .Where(progress => progress.UserId == userId && requiredIds.Contains(progress.LessonId))
            .ToListAsync(cancellationToken);
        var byLesson = rows.ToDictionary(row => row.LessonId, StringComparer.Ordinal);

        return required
            .Where(lesson =>
            {
                byLesson.TryGetValue(lesson.Id, out var row);
                return row is null
                    || row.Status != LessonProgress.StatusCompleted
                    || row.CompletedVersion != lesson.Version;
            })
            .Select(lesson => new RequiredLessonRef(lesson.Id, lesson.Version))
            .ToList();
    }
}

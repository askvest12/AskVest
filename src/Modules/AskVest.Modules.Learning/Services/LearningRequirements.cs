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

    public Task<IReadOnlyList<LessonSearchHit>> SearchLessonsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var needle = query.Trim();
        if (needle.Length == 0 || limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<LessonSearchHit>>([]);
        }

        var hits = snapshot.Tracks
            .SelectMany(track => track.Lessons)
            .Select(lesson => (Lesson: lesson, Excerpt: ExcerptFor(lesson, needle)))
            .Where(pair => pair.Excerpt is not null)
            .OrderBy(pair => pair.Lesson.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(pair => new LessonSearchHit(
                pair.Lesson.Id,
                pair.Lesson.Title,
                pair.Lesson.Summary,
                pair.Excerpt!))
            .ToList();
        return Task.FromResult<IReadOnlyList<LessonSearchHit>>(hits);
    }

    private static string? ExcerptFor(LessonContent lesson, string needle)
    {
        if (lesson.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || lesson.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || lesson.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return lesson.Summary.Length <= 160 ? lesson.Summary : lesson.Summary[..160];
        }

        var match = lesson.Blocks
            .Select(block => block.Text ?? string.Join(' ', block.Items ?? []))
            .FirstOrDefault(text => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return null;
        }

        var index = match.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, index - 60);
        var excerpt = match[start..Math.Min(match.Length, start + 160)];
        return excerpt;
    }
}

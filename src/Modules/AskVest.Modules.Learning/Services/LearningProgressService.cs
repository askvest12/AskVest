using AskVest.Modules.Learning.Content;
using AskVest.Modules.Learning.Domain;
using AskVest.Modules.Learning.Persistence;
using AskVest.Modules.Learning.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Learning.Services;

internal sealed class LearningProgressService(
    LearningDbContext db,
    LearningContentSnapshot snapshot,
    TimeProvider timeProvider)
{
    public async Task<LearningProgressView> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var progress = await db.LessonProgress
            .Where(row => row.UserId == userId)
            .ToListAsync(cancellationToken);
        var days = await db.LearningDays
            .Where(day => day.UserId == userId)
            .Select(day => day.Day)
            .ToListAsync(cancellationToken);

        var today = Today();
        var streak = Streak.Compute(days, today);
        var byLesson = progress.ToDictionary(row => row.LessonId, StringComparer.Ordinal);

        var tracks = snapshot.Tracks.Select(track =>
        {
            var rows = track.Lessons
                .Select(lesson => byLesson.GetValueOrDefault(lesson.Id))
                .ToList();
            return new TrackProgress(
                track.Id,
                track.Title,
                track.Lessons.Count,
                rows.Count(row => row?.Status == LessonProgress.StatusCompleted),
                rows.Count(row => row is not null));
        }).ToList();

        var required = snapshot.RequiredLessons()
            .Select(lesson =>
            {
                byLesson.TryGetValue(lesson.Id, out var row);
                return new RequiredLessonProgress(
                    lesson.Id,
                    snapshot.TrackByLessonId.GetValueOrDefault(lesson.Id),
                    lesson.Version,
                    LearningReadService.StatusFor(row),
                    row?.CompletedVersion);
            })
            .ToList();

        return new LearningProgressView(
            tracks,
            streak.Current,
            streak.Longest,
            days.Contains(today),
            required);
    }

    private DateOnly Today()
        => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
}

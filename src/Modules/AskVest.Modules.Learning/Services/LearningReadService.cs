using AskVest.Modules.Learning.Content;
using AskVest.Modules.Learning.Persistence;
using AskVest.Modules.Learning.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Learning.Services;

internal sealed class LearningReadService(
    LearningDbContext dbContext,
    LearningContentSnapshot snapshot)
{
    public IReadOnlyList<TrackContent> Tracks => snapshot.Tracks;

    public IReadOnlyDictionary<string, string> TrackIdByLesson => snapshot.TrackByLessonId;

    public bool TryGetLesson(string lessonId, out LessonContent? lesson)
        => snapshot.TryGetLesson(lessonId, out lesson);

    public async Task<IReadOnlyDictionary<string, LessonProgress>> GetProgressAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.LessonProgress
            .Where(progress => progress.UserId == userId)
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(progress => progress.LessonId, StringComparer.Ordinal);
    }

    public static string StatusFor(LessonProgress? progress)
        => progress?.Status ?? "not_started";
}

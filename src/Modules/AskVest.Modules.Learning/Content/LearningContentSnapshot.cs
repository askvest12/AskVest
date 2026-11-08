namespace AskVest.Modules.Learning.Content;

/// <summary>
/// Immutable startup snapshot of all lesson content. Built once from the
/// reviewed data files; requests only read it.
/// </summary>
internal sealed class LearningContentSnapshot
{
    private readonly Dictionary<string, LessonContent> _lessonsById;
    private readonly Dictionary<string, TrackContent> _tracksById;

    public LearningContentSnapshot(IReadOnlyList<TrackContent> tracks)
    {
        Tracks = tracks;
        _tracksById = tracks.ToDictionary(track => track.Id, StringComparer.Ordinal);
        _lessonsById = tracks
            .SelectMany(track => track.Lessons.Select(lesson => (lesson, track)))
            .ToDictionary(pair => pair.lesson.Id, pair => pair.lesson, StringComparer.Ordinal);
        TrackByLessonId = tracks
            .SelectMany(track => track.Lessons.Select(lesson => (LessonId: lesson.Id, TrackId: track.Id)))
            .ToDictionary(pair => pair.LessonId, pair => pair.TrackId, StringComparer.Ordinal);
    }

    public IReadOnlyList<TrackContent> Tracks { get; }

    public IReadOnlyDictionary<string, string> TrackByLessonId { get; }

    public bool TryGetLesson(string lessonId, out LessonContent? lesson)
        => _lessonsById.TryGetValue(lessonId, out lesson);

    public TrackContent? GetTrack(string trackId)
        => _tracksById.GetValueOrDefault(trackId);

    public IEnumerable<LessonContent> RequiredLessons()
        => _lessonsById.Values.Where(lesson => lesson.RequiredForReal);
}

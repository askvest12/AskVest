namespace AskVest.Modules.Learning.Services;

public sealed record LearningProgressView(
    IReadOnlyList<TrackProgress> Tracks,
    int CurrentStreak,
    int LongestStreak,
    bool TodayCounted,
    IReadOnlyList<RequiredLessonProgress> RequiredLessons);

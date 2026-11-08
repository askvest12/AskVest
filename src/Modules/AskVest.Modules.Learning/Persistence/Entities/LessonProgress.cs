namespace AskVest.Modules.Learning.Persistence.Entities;

public sealed class LessonProgress
{
    public const string StatusAttempted = "attempted";
    public const string StatusCompleted = "completed";

    public Guid UserId { get; set; }

    public string LessonId { get; set; } = string.Empty;

    public string Status { get; set; } = StatusAttempted;

    public int? CompletedVersion { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int? BestScore { get; set; }

    public int AttemptsCount { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Xmin { get; set; }
}

namespace AskVest.Modules.Learning.Persistence.Entities;

public sealed class QuizAttempt
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string LessonId { get; set; } = string.Empty;

    public int LessonVersion { get; set; }

    public int Score { get; set; }

    public bool Passed { get; set; }

    public string Answers { get; set; } = string.Empty;

    public DateTimeOffset SubmittedAt { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;
}

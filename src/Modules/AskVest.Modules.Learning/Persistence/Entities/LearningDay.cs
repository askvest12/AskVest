namespace AskVest.Modules.Learning.Persistence.Entities;

public sealed class LearningDay
{
    public Guid UserId { get; set; }

    public DateOnly Day { get; set; }

    public DateTimeOffset FirstActivityAt { get; set; }
}

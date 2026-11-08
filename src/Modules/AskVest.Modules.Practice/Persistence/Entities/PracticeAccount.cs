namespace AskVest.Modules.Practice.Persistence.Entities;

public sealed class PracticeAccount
{
    public Guid UserId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public int ResetCount { get; set; }

    public DateTimeOffset? LastResetAt { get; set; }

    public decimal StartingCreditsUsed { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Xmin { get; set; }
}

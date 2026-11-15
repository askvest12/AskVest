namespace AskVest.Modules.Funding.Persistence.Entities;

/// <summary>
/// A stored withdrawal pause for one currency. Set by the reserves check
/// after consecutive shortfalls or by an operator; survives restarts.
/// </summary>
public sealed class FundingPause
{
    public Guid Id { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string ReasonCode { get; set; } = string.Empty;

    public DateTimeOffset? PausedAt { get; set; }

    public DateTimeOffset? ResumedAt { get; set; }

    public int ConsecutiveShortfalls { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

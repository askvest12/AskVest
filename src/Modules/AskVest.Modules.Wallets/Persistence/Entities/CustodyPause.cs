namespace AskVest.Modules.Wallets.Persistence.Entities;

/// <summary>
/// A custody pause scope: one row per chain and optional token. The
/// consecutive mismatch counter persists across restarts; PausedAt set means
/// withdrawals for the scope are paused until an operator resumes it.
/// </summary>
public sealed class CustodyPause
{
    public Guid Id { get; set; }

    public string ChainId { get; set; } = string.Empty;

    public Guid? TokenId { get; set; }

    public string ReasonCode { get; set; } = string.Empty;

    public int ConsecutiveMismatches { get; set; }

    public DateTimeOffset? PausedAt { get; set; }

    public DateTimeOffset? ResumedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

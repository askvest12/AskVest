namespace AskVest.Modules.Funding.Persistence.Entities;

/// <summary>
/// A funding restriction blocks new cash withdrawals until an operator
/// clears it. Created by reversals and other risk events.
/// </summary>
public sealed class FundingRestriction
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string ReasonCode { get; set; } = string.Empty;

    public DateTimeOffset Since { get; set; }

    public DateTimeOffset? ClearedAt { get; set; }
}

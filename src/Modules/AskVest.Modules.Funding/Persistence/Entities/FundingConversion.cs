using AskVest.Modules.Funding.Domain;

namespace AskVest.Modules.Funding.Persistence.Entities;

public sealed class FundingConversion
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Pair key in "FROM:TO" form.</summary>
    public string Pair { get; set; } = string.Empty;

    public string SourceCurrency { get; set; } = string.Empty;

    public string TargetCurrency { get; set; } = string.Empty;

    public decimal SourceAmount { get; set; }

    public decimal MidRate { get; set; }

    public decimal EffectiveRate { get; set; }

    /// <summary>Target amount after spread, before fee.</summary>
    public decimal ReceiveAmount { get; set; }

    public decimal FeeAmount { get; set; }

    /// <summary>What the user actually receives: receive minus fee.</summary>
    public decimal NetAmount { get; set; }

    public string TermsHash { get; set; } = string.Empty;

    public ConversionState State { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set only on completion; unique per user.</summary>
    public string? IdempotencyKey { get; set; }

    public Guid? JournalId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

using AskVest.Modules.Funding.Domain;

namespace AskVest.Modules.Funding.Persistence.Entities;

public sealed class FundingWithdrawal
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public Guid BeneficiaryId { get; set; }

    /// <summary>What the beneficiary receives.</summary>
    public decimal ReceiveAmount { get; set; }

    public decimal FeeAmount { get; set; }

    /// <summary>What leaves the user's available balance: receive plus fee.</summary>
    public decimal TotalDebited { get; set; }

    public string TermsHash { get; set; } = string.Empty;

    public WithdrawalState State { get; set; }

    /// <summary>Preview expiry; placement checks it again.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public bool PreviewUsed { get; set; }

    public Guid? HoldJournalId { get; set; }

    public Guid? SettleJournalId { get; set; }

    public Guid? ReleaseJournalId { get; set; }

    /// <summary>Our idempotency key sent to the provider.</summary>
    public string? ProviderReference { get; set; }

    public string? DetailCode { get; set; }

    public string? ReviewDecision { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RequestedAt { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Postgres row version for optimistic concurrency.</summary>
    public uint Xmin { get; set; }
}

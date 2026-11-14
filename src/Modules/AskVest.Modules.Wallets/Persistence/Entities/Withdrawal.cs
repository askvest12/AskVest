using AskVest.Modules.Wallets.Domain;

namespace AskVest.Modules.Wallets.Persistence.Entities;

/// <summary>
/// A withdrawal preview or committed withdrawal. Previewed rows carry the
/// terms the user saw plus a terms hash the step-up grant binds to; placing
/// consumes the preview and reserves the gross quantity in the ledger.
/// Provider references stay internal; the settlement reference is the only
/// provider-side value ever exposed.
/// </summary>
public sealed class Withdrawal
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid TokenId { get; set; }

    public Guid AddressId { get; set; }

    public string DestinationChainId { get; set; } = string.Empty;

    public string DestinationAddress { get; set; } = string.Empty;

    public decimal GrossQuantity { get; set; }

    public decimal Fee { get; set; }

    public decimal NetQuantity { get; set; }

    public string TermsHash { get; set; } = string.Empty;

    public WithdrawalState State { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public bool PreviewUsed { get; set; }

    public Guid? HoldJournalId { get; set; }

    public Guid? SettleJournalId { get; set; }

    public Guid? ReleaseJournalId { get; set; }

    public string? ProviderReference { get; set; }

    public string? SettlementReference { get; set; }

    public string? DetailCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RequestedAt { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public DateTimeOffset? LastStatusCheckAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Xmin { get; set; }
}

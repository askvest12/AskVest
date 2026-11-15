using AskVest.Modules.Funding.Domain;

namespace AskVest.Modules.Funding.Persistence.Entities;

public sealed class FundingDeposit
{
    public Guid Id { get; set; }

    public string ProviderKey { get; set; } = string.Empty;

    public string ProviderDepositId { get; set; } = string.Empty;

    /// <summary>Empty when the account reference matched no user.</summary>
    public Guid UserId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public decimal ProviderFee { get; set; }

    public bool PayerVerified { get; set; }

    public DepositState State { get; set; }

    public string? DetailCode { get; set; }

    public Guid? CreditJournalId { get; set; }

    public Guid? ReversalJournalId { get; set; }

    /// <summary>Amount that could not be recovered on reversal.</summary>
    public decimal Shortfall { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CreditedAt { get; set; }

    public DateTimeOffset? ReversedAt { get; set; }
}

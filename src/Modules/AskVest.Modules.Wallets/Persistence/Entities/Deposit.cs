using AskVest.Modules.Wallets.Domain;

namespace AskVest.Modules.Wallets.Persistence.Entities;

/// <summary>
/// One provider-reported deposit. UserId and TokenId stay empty when the
/// destination or token cannot be resolved; such rows are stored with a
/// reason and never credited.
/// </summary>
public sealed class Deposit
{
    public Guid Id { get; set; }

    public string ProviderKey { get; set; } = string.Empty;

    public string ProviderDepositId { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public string ChainId { get; set; } = string.Empty;

    public Guid TokenId { get; set; }

    /// <summary>
    /// The ledger asset code credited, set on credit for both catalog token
    /// and approved stablecoin deposits. Empty until credited.
    /// </summary>
    public string AssetCode { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public string SenderAddress { get; set; } = string.Empty;

    public string? SettlementReference { get; set; }

    public DepositState State { get; set; }

    public string? DetailCode { get; set; }

    public Guid? CreditJournalId { get; set; }

    public DateTimeOffset DetectedAt { get; set; }

    public DateTimeOffset? CreditedAt { get; set; }
}

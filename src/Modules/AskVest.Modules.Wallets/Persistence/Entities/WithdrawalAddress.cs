using AskVest.Modules.Wallets.Domain;

namespace AskVest.Modules.Wallets.Persistence.Entities;

/// <summary>
/// A whitelisted withdrawal destination. A new address starts in cooling_off
/// and becomes active only after the configured period; removal is terminal.
/// </summary>
public sealed class WithdrawalAddress
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string ChainId { get; set; } = string.Empty;

    public string CanonicalAddress { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public WithdrawalAddressState State { get; set; }

    public string? ScreeningResult { get; set; }

    public string? ScreeningReference { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ActiveFrom { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }
}

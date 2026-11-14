namespace AskVest.Modules.Wallets.Persistence.Entities;

/// <summary>
/// A per-user deposit address on one chain, issued by the custody provider.
/// The address and memo are opaque provider data; the provider reference is
/// internal and never exposed.
/// </summary>
public sealed class WalletAddress
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string ChainId { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string Memo { get; set; } = string.Empty;

    public string ProviderReference { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

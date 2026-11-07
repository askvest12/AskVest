using AskVest.Modules.Ledger.Contracts;

namespace AskVest.Modules.Ledger.Persistence.Entities;

public sealed class Account
{
    public static readonly Guid SystemOwnerId = Guid.Empty;

    public Guid Id { get; set; }

    public Book Book { get; set; }

    public AccountOwnerType OwnerType { get; set; }

    public Guid OwnerId { get; set; }

    public AccountKind Kind { get; set; }

    public string AssetCode { get; set; } = string.Empty;

    public bool AllowNegative { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

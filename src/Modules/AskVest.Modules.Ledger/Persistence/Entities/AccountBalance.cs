namespace AskVest.Modules.Ledger.Persistence.Entities;

public sealed class AccountBalance
{
    public Guid AccountId { get; set; }

    public decimal Balance { get; set; }

    public bool AllowNegative { get; set; }

    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

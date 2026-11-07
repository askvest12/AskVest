using AskVest.Modules.Ledger.Contracts;

namespace AskVest.Modules.Ledger.Persistence.Entities;

public sealed class JournalEntry
{
    public Guid Id { get; set; }

    public Guid JournalId { get; set; }

    public Book Book { get; set; }

    public Guid AccountId { get; set; }

    public string AssetCode { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public int LineNumber { get; set; }
}

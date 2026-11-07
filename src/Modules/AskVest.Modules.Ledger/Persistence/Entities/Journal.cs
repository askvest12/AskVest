using AskVest.Modules.Ledger.Domain.Enums;

namespace AskVest.Modules.Ledger.Persistence.Entities;

public sealed class Journal
{
    public Guid Id { get; set; }

    public Book Book { get; set; }

    public string ReasonCode { get; set; } = string.Empty;

    public string ReferenceType { get; set; } = string.Empty;

    public Guid? ReferenceId { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public Guid? ReversesJournalId { get; set; }

    public Guid? CorrelationId { get; set; }

    public DateTimeOffset PostedAt { get; set; }
}

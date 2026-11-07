namespace AskVest.Modules.Ledger.Contracts;

public sealed record LedgerJournal(
    Guid Id,
    Book Book,
    string ReasonCode,
    string ReferenceType,
    Guid? ReferenceId,
    DateTimeOffset PostedAt);

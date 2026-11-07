namespace AskVest.Modules.Ledger.Contracts;

public sealed record LedgerJournalPostedMessage(
    Guid JournalId,
    Book Book,
    string ReasonCode,
    string ReferenceType,
    Guid? ReferenceId,
    IReadOnlyList<Guid> UserIds);

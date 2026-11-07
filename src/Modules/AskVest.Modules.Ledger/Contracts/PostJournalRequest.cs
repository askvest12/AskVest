namespace AskVest.Modules.Ledger.Contracts;

public sealed record PostJournalRequest(
    string IdempotencyKey,
    Book Book,
    string ReasonCode,
    string ReferenceType,
    Guid? ReferenceId,
    IReadOnlyList<LedgerLine> Lines);

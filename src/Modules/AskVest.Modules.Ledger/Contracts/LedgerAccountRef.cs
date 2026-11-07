namespace AskVest.Modules.Ledger.Contracts;

public sealed record LedgerAccountRef(
    Book Book,
    AccountOwnerType OwnerType,
    Guid OwnerId,
    AccountKind Kind);

namespace AskVest.Modules.Ledger.Contracts;

public static class LedgerAccounts
{
    public static LedgerAccountRef UserAvailable(Book book, Guid userId)
        => new(book, AccountOwnerType.User, userId, AccountKind.UserAvailable);

    public static LedgerAccountRef UserHeld(Book book, Guid userId)
        => new(book, AccountOwnerType.User, userId, AccountKind.UserHeld);

    public static LedgerAccountRef SystemClearing(Book book)
        => new(book, AccountOwnerType.System, Guid.Empty, AccountKind.SystemClearing);

    public static LedgerAccountRef SystemFees(Book book)
        => new(book, AccountOwnerType.System, Guid.Empty, AccountKind.SystemFees);

    public static LedgerAccountRef PracticeIssuance(Book book)
        => new(book, AccountOwnerType.System, Guid.Empty, AccountKind.SystemPracticeIssuance);
}

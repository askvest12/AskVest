using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Ledger.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Ledger.Services;

internal sealed class LedgerDeletionBlocker(LedgerDbContext db) : IDeletionBlocker
{
    public async Task<IReadOnlyList<string>> GetBlockReasonsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var blocked = await db.AccountBalances
            .Join(
                db.Accounts,
                balance => balance.AccountId,
                account => account.Id,
                (balance, account) => new { balance.Balance, account })
            .AnyAsync(row => row.account.OwnerType == AccountOwnerType.User
                && row.account.OwnerId == userId
                && row.account.Book == Book.Real
                && (row.account.Kind == AccountKind.UserAvailable || row.account.Kind == AccountKind.UserHeld)
                && row.Balance != 0m,
                cancellationToken);
        return blocked ? ["real_balance_remaining"] : [];
    }
}

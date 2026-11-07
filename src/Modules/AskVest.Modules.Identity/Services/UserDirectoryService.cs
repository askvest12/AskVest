using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Identity.Services;

public sealed class UserDirectoryService(IdentityDbContext db) : IUserDirectory
{
    public async Task<UserAccountSnapshot?> FindAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var account = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.Status, u.EmailVerifiedAt })
            .FirstOrDefaultAsync(cancellationToken);
        return account is null
            ? null
            : new UserAccountSnapshot(account.Status == UserStatus.Active, account.EmailVerifiedAt is not null);
    }
}

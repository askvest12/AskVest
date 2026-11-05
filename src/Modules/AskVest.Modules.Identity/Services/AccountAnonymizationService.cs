using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AskVest.Modules.Identity.Services;

public sealed class AccountAnonymizationService(
    IdentityDbContext db,
    SecurityEventRecorder securityEvents,
    TimeProvider timeProvider,
    ILogger<AccountAnonymizationService> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var due = await db.Users
            .Where(u => u.Status == UserStatus.DeletionRequested
                && u.DeletionScheduledFor != null
                && u.DeletionScheduledFor <= now)
            .ToListAsync(cancellationToken);

        var anonymized = 0;
        foreach (var user in due)
        {
            await AnonymizeAsync(user, cancellationToken);
            anonymized++;
        }

        if (anonymized > 0 && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Anonymized {Count} accounts past their deletion date.", anonymized);
        }

        return anonymized;
    }

    private async Task AnonymizeAsync(Persistence.Entities.UserAccount user, CancellationToken cancellationToken)
    {
        var placeholder = $"deleted-{user.Id:N}@anonymized.invalid";
        user.NormalizedEmail = placeholder;
        user.EmailVerifiedAt = null;
        user.PasswordHash = string.Empty;
        user.PasswordPepperVersion = 0;
        user.PhoneE164 = null;
        user.PhoneVerifiedAt = null;
        user.PinHash = null;
        user.PinSetAt = null;
        user.PinFailedCount = 0;
        user.PinLockedUntil = null;
        user.PinLockRepeatCount = 0;
        user.Status = UserStatus.Anonymized;
        user.UpdatedAt = timeProvider.GetUtcNow();

        var sessions = await db.Sessions
            .Where(s => s.UserId == user.Id)
            .ToListAsync(cancellationToken);
        var sessionIds = sessions.Select(s => s.Id).ToList();

        var tokens = await db.RefreshTokens
            .Where(t => sessionIds.Contains(t.SessionId))
            .ToListAsync(cancellationToken);
        db.RefreshTokens.RemoveRange(tokens);
        db.Sessions.RemoveRange(sessions);
        var grants = await db.StepUpGrants
            .Where(g => g.UserId == user.Id || sessionIds.Contains(g.SessionId))
            .ToListAsync(cancellationToken);
        db.StepUpGrants.RemoveRange(grants);
        var devices = await db.Devices
            .Where(d => d.UserId == user.Id)
            .ToListAsync(cancellationToken);
        db.Devices.RemoveRange(devices);

        await db.SaveChangesAsync(cancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"UPDATE identity.security_events SET ip = NULL, user_agent = NULL WHERE user_id = {user.Id}",
            cancellationToken);

        securityEvents.Record(SecurityEventType.AccountAnonymized, user.Id, null, null, null);
        await db.SaveChangesAsync(cancellationToken);
    }
}

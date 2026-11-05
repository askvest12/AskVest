using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Services;

public sealed class OneTimeCodeService(
    IdentityDbContext db,
    IOptions<IdentityOptions> options,
    ISecureRandom random,
    IOneTimeCodeHasher codeHasher,
    TimeProvider timeProvider)
{
    private readonly IdentityOptions _options = options.Value;

    public async Task<string> IssueAsync(
        Guid userId,
        OneTimeCodePurpose purpose,
        CodeDestinationType destinationType,
        string destination,
        Guid? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        var stale = await db.OneTimeCodes
            .Where(c => c.UserId == userId && c.Purpose == purpose && c.ConsumedAt == null)
            .ToListAsync(cancellationToken);
        db.OneTimeCodes.RemoveRange(stale);

        var code = random.NewOneTimeCode(_options.PinLength == 6 ? 6 : 6);
        var entity = new OneTimeCode
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Purpose = purpose,
            DestinationType = destinationType,
            Destination = destination,
            CodeHash = codeHasher.Hash(userId, purpose, code),
            AttemptsUsed = 0,
            AttemptsAllowed = _options.OneTimeCodeMaxAttempts,
            ExpiresAt = timeProvider.GetUtcNow().Add(_options.OneTimeCodeLifetime),
            DeviceId = deviceId,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.OneTimeCodes.Add(entity);
        return code;
    }

    public async Task<bool> CanSendAsync(
        Guid userId,
        OneTimeCodePurpose purpose,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var codes = await db.OneTimeCodes
            .Where(c => c.UserId == userId && c.Purpose == purpose && c.Destination == destination)
            .Select(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        if (codes.Any(created => now - created < _options.ResendCooldown))
        {
            return false;
        }

        var hourAgo = now.AddHours(-1);
        return codes.Count(created => created > hourAgo) < _options.CodesPerDestinationPerHour;
    }

    public async Task<bool> TryVerifyAsync(
        Guid userId,
        OneTimeCodePurpose purpose,
        string code,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var candidate = await db.Database.SqlQuery<Guid>($@"
            UPDATE identity.one_time_codes
            SET attempts_used = attempts_used + 1
            WHERE id = (
                SELECT id FROM identity.one_time_codes
                WHERE user_id = {userId}
                  AND purpose = {purpose.ToString()}
                  AND consumed_at IS NULL
                  AND expires_at > {now}
                ORDER BY created_at DESC
                LIMIT 1
            )
            AND consumed_at IS NULL
            AND expires_at > {now}
            AND attempts_used < attempts_allowed
            RETURNING id")
            .ToListAsync(cancellationToken);

        if (candidate.Count == 0)
        {
            return false;
        }

        var codeId = candidate[0];
        var storedHash = await db.Database
            .SqlQuery<string>($"SELECT code_hash AS \"Value\" FROM identity.one_time_codes WHERE id = {codeId}")
            .SingleAsync(cancellationToken);

        if (!codeHasher.Verify(userId, purpose, code, storedHash))
        {
            return false;
        }

        var consumed = await db.Database.ExecuteSqlAsync($@"
            UPDATE identity.one_time_codes
            SET consumed_at = {now}
            WHERE id = {codeId}
              AND consumed_at IS NULL", cancellationToken);
        return consumed == 1;
    }
}

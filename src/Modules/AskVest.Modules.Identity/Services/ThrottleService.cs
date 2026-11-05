using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Services;

public sealed class ThrottleService(
    IdentityDbContext db,
    IKeyedHasher keyedHasher,
    IOptions<IdentityOptions> options,
    TimeProvider timeProvider)
{
    private readonly IdentityOptions _options = options.Value;

    public async Task<TimeSpan?> LockedForAsync(
        ThrottleKind kind,
        string identifier,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var keyHash = KeyHash(kind, identifier);
        var bucket = await db.ThrottleBuckets
            .FirstOrDefaultAsync(b => b.Kind == kind && b.KeyHash == keyHash, cancellationToken);
        if (bucket?.LockedUntil is null || bucket.LockedUntil <= now)
        {
            return null;
        }

        return bucket.LockedUntil.Value - now;
    }

    public async Task RecordFailureAsync(
        ThrottleKind kind,
        string identifier,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var keyHash = KeyHash(kind, identifier);
        var bucket = await db.ThrottleBuckets
            .FirstOrDefaultAsync(b => b.Kind == kind && b.KeyHash == keyHash, cancellationToken);

        if (bucket is null)
        {
            bucket = new ThrottleBucket
            {
                Id = Guid.CreateVersion7(),
                Kind = kind,
                KeyHash = keyHash,
                FailureCount = 0,
                WindowStart = now,
            };
            db.ThrottleBuckets.Add(bucket);
        }
        else if (now - bucket.WindowStart > _options.LoginThrottleWindow)
        {
            bucket.FailureCount = 0;
            bucket.WindowStart = now;
            bucket.LockedUntil = null;
            bucket.LockRepeatCount = 0;
        }

        bucket.FailureCount++;
        var limit = kind == ThrottleKind.Account ? _options.LoginFailuresPerAccount : _options.LoginFailuresPerClientIp;
        if (bucket.FailureCount >= limit)
        {
            var duration = _options.LoginThrottleDuration * Math.Pow(2, bucket.LockRepeatCount);
            var capped = duration > _options.LoginThrottleMaxDuration ? _options.LoginThrottleMaxDuration : duration;
            bucket.LockRepeatCount++;
            bucket.LockedUntil = now.Add(capped);
        }
    }

    public async Task ResetAsync(ThrottleKind kind, string identifier, CancellationToken cancellationToken)
    {
        var keyHash = KeyHash(kind, identifier);
        var bucket = await db.ThrottleBuckets
            .FirstOrDefaultAsync(b => b.Kind == kind && b.KeyHash == keyHash, cancellationToken);
        if (bucket is not null)
        {
            db.ThrottleBuckets.Remove(bucket);
        }
    }

    private string KeyHash(ThrottleKind kind, string identifier)
        => keyedHasher.Hash($"throttle:{kind}", identifier);
}

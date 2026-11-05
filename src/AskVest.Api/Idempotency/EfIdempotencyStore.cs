using System.Security.Cryptography;
using System.Text;
using AskVest.Platform;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Api;

internal static class RequestFingerprint
{
    internal static string Compute(string method, string path, ReadOnlySpan<byte> body)
    {
        var payload = Encoding.UTF8.GetBytes($"{method}\n{path}\n");
        var hash = SHA256.HashData([.. payload, .. body]);
        return Convert.ToHexStringLower(hash);
    }
}

internal sealed class EfIdempotencyStore(PlatformDbContext db, TimeProvider timeProvider) : IIdempotencyStore
{
    public async Task<IdempotencyBeginResult> BeginAsync(
        string scope,
        string key,
        string method,
        string path,
        string fingerprint,
        TimeSpan lockDuration,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var existing = await db.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.Scope == scope && r.IdempotencyKey == key, cancellationToken);

        if (existing is null)
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.CreateVersion7(),
                Scope = scope,
                IdempotencyKey = key,
                RequestMethod = method,
                RequestPath = path,
                RequestFingerprint = fingerprint,
                State = IdempotencyStates.InProgress,
                LockedUntil = now + lockDuration,
                CreatedAt = now,
                ExpiresAt = expiresAt,
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return IdempotencyBeginResult.Acquired;
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }
        else
        {
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return IdempotencyBeginResult.KeyReused;
            }

            if (existing.State == IdempotencyStates.Completed)
            {
                return IdempotencyBeginResult.Replay;
            }

            if (existing.LockedUntil is { } lockedUntil && lockedUntil > now)
            {
                return IdempotencyBeginResult.InProgress;
            }

            existing.State = IdempotencyStates.InProgress;
            existing.LockedUntil = now + lockDuration;
            existing.ResponseStatusCode = null;
            existing.ResponseContentType = null;
            existing.ResponseBody = null;
            existing.ResponseBodyOmitted = false;
            existing.CompletedAt = null;
            await db.SaveChangesAsync(cancellationToken);
            return IdempotencyBeginResult.Acquired;
        }

        var raced = await db.IdempotencyRecords
            .FirstAsync(r => r.Scope == scope && r.IdempotencyKey == key, cancellationToken);
        if (!string.Equals(raced.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return IdempotencyBeginResult.KeyReused;
        }

        return raced.State == IdempotencyStates.Completed
            ? IdempotencyBeginResult.Replay
            : IdempotencyBeginResult.InProgress;
    }

    public async Task<IdempotencyStoredResponse?> GetCompletedAsync(
        string scope,
        string key,
        CancellationToken cancellationToken)
    {
        var record = await db.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.Scope == scope && r.IdempotencyKey == key, cancellationToken);
        if (record is null || record.State != IdempotencyStates.Completed)
        {
            return null;
        }

        return new IdempotencyStoredResponse(
            record.ResponseStatusCode ?? 0,
            record.ResponseContentType,
            record.ResponseBody,
            record.ResponseBodyOmitted);
    }

    public async Task CompleteAsync(
        string scope,
        string key,
        IdempotencyStoredResponse response,
        CancellationToken cancellationToken)
    {
        var record = await db.IdempotencyRecords
            .FirstAsync(r => r.Scope == scope && r.IdempotencyKey == key, cancellationToken);
        record.State = IdempotencyStates.Completed;
        record.ResponseStatusCode = response.StatusCode;
        record.ResponseContentType = response.ContentType;
        record.ResponseBody = response.Body;
        record.ResponseBodyOmitted = response.BodyOmitted;
        record.CompletedAt = timeProvider.GetUtcNow();
        record.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReleaseAsync(string scope, string key, CancellationToken cancellationToken)
    {
        var record = await db.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.Scope == scope && r.IdempotencyKey == key, cancellationToken);
        if (record is not null)
        {
            db.IdempotencyRecords.Remove(record);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await db.IdempotencyRecords
            .Where(record => record.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);
    }
}

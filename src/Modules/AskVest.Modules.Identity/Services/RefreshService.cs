using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Messaging;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Services;

public sealed record RefreshedSession(
    Guid SessionId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshIdleExpiresAt);

public sealed class RefreshService(
    IdentityDbContext db,
    IAccessTokenService accessTokenService,
    ISecureRandom secureRandom,
    IKeyedHasher keyedHasher,
    SecurityEventRecorder securityEvents,
    IMessageChannel messageChannel,
    IOptions<IdentityOptions> options,
    TimeProvider timeProvider)
{
    private static readonly Error RefreshInvalid = new(
        ErrorCodes.AuthRefreshInvalid,
        "The refresh token is not valid.",
        ErrorType.Unauthenticated);

    private static readonly Error RefreshConflict = new(
        ErrorCodes.AuthRefreshConflict,
        "The refresh token was already rotated.",
        ErrorType.Conflict);

    private readonly IdentityOptions _options = options.Value;

    public async Task<Result<RefreshedSession>> RefreshAsync(
        RefreshRequest request,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var tokenHash = keyedHasher.Sha256Hex(request.RefreshToken!);
        var token = await db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        if (token is null)
        {
            return Result.Failure<RefreshedSession>(RefreshInvalid);
        }

        var session = await db.Sessions
            .FirstOrDefaultAsync(s => s.Id == token.SessionId, cancellationToken);
        if (session is null || session.RevokedAt is not null || session.AbsoluteExpiresAt <= now)
        {
            return Result.Failure<RefreshedSession>(RefreshInvalid);
        }

        var installationId = await db.Devices
            .Where(d => d.Id == session.DeviceId)
            .Select(d => d.InstallationId)
            .FirstOrDefaultAsync(cancellationToken);
        var sameDevice = string.Equals(
            installationId,
            request.InstallationId?.Trim(),
            StringComparison.Ordinal);

        if (token.RotatedAt is { } rotatedAt)
        {
            if (now - rotatedAt <= _options.RefreshReuseGrace && sameDevice)
            {
                return Result.Failure<RefreshedSession>(RefreshConflict);
            }

            await HandleReuseAsync(session, ip, userAgent, correlationId, cancellationToken);
            return Result.Failure<RefreshedSession>(RefreshInvalid);
        }

        if (token.IdleExpiresAt <= now)
        {
            return Result.Failure<RefreshedSession>(RefreshInvalid);
        }

        var replacementId = Guid.CreateVersion7();
        var newRefreshToken = secureRandom.NewToken(32);
        var idleExpiry = now.Add(_options.RefreshIdleLifetime);
        if (idleExpiry > session.AbsoluteExpiresAt)
        {
            idleExpiry = session.AbsoluteExpiresAt;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var replacement = new RefreshToken
        {
            Id = replacementId,
            SessionId = session.Id,
            TokenHash = keyedHasher.Sha256Hex(newRefreshToken),
            IssuedAt = now,
            IdleExpiresAt = idleExpiry,
        };
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync(cancellationToken);

        var rotated = await db.Database.ExecuteSqlAsync($@"
            UPDATE identity.refresh_tokens
            SET rotated_at = {now}, replaced_by_id = {replacementId}
            WHERE id = {token.Id} AND rotated_at IS NULL",
            cancellationToken);
        if (rotated == 0)
        {
            db.Entry(replacement).State = EntityState.Detached;
            await transaction.RollbackAsync(cancellationToken);
            var actualRotatedAt = await db.RefreshTokens
                .Where(t => t.Id == token.Id)
                .Select(t => t.RotatedAt)
                .FirstAsync(cancellationToken);
            if (actualRotatedAt is { } winnerRotatedAt
                && now - winnerRotatedAt <= _options.RefreshReuseGrace
                && sameDevice)
            {
                return Result.Failure<RefreshedSession>(RefreshConflict);
            }

            await HandleReuseAsync(session, ip, userAgent, correlationId, cancellationToken);
            return Result.Failure<RefreshedSession>(RefreshInvalid);
        }

        session.LastUsedAt = now;
        var user = await db.Users.FirstAsync(u => u.Id == session.UserId, cancellationToken);
        var access = accessTokenService.Issue(user.Id, session.Id);
        securityEvents.Record(SecurityEventType.RefreshRotated, user.Id, ip, userAgent, correlationId, session.DeviceId);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success<RefreshedSession>(new RefreshedSession(
            session.Id,
            access.Token,
            access.ExpiresAt,
            newRefreshToken,
            idleExpiry));
    }

    private async Task HandleReuseAsync(
        UserSession session,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (session.RevokedAt is null)
        {
            session.RevokedAt = now;
            session.RevokedReason = "refresh_reuse";
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == session.UserId, cancellationToken);
        securityEvents.Record(
            SecurityEventType.RefreshReuseDetected,
            session.UserId,
            ip,
            userAgent,
            correlationId,
            session.DeviceId);
        if (user is not null)
        {
            messageChannel.TryEnqueue(new QueuedEmail(MessageTemplates.RefreshReuseDetected(user.NormalizedEmail)));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

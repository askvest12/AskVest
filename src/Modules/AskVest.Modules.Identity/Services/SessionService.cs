using System.Security.Cryptography;
using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Services;

public sealed record IssuedSession(
    Guid SessionId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshIdleExpiresAt);

public sealed class SessionService(
    IdentityDbContext db,
    IAccessTokenService accessTokenService,
    ISecureRandom secureRandom,
    IKeyedHasher keyedHasher,
    SecurityEventRecorder securityEvents,
    IOptions<IdentityOptions> options,
    TimeProvider timeProvider)
{
    private readonly IdentityOptions _options = options.Value;

    public async Task<IssuedSession> CreateAsync(
        UserAccount user,
        Device device,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var activeSessions = await db.Sessions
            .Where(s => s.UserId == user.Id && s.RevokedAt == null && s.AbsoluteExpiresAt > now)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        var overflow = activeSessions.Count - _options.MaxActiveSessions + 1;
        if (overflow > 0)
        {
            foreach (var oldest in activeSessions.Take(overflow))
            {
                oldest.RevokedAt = now;
                oldest.RevokedReason = "session_cap";
            }
        }

        var session = new UserSession
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            DeviceId = device.Id,
            CreatedAt = now,
            LastUsedAt = now,
            AbsoluteExpiresAt = now.Add(_options.RefreshAbsoluteLifetime),
            CreationIp = ip,
            UserAgent = userAgent,
        };
        db.Sessions.Add(session);

        var refreshToken = secureRandom.NewToken(32);
        var refresh = new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            SessionId = session.Id,
            TokenHash = keyedHasher.Sha256Hex(refreshToken),
            IssuedAt = now,
            IdleExpiresAt = now.Add(_options.RefreshIdleLifetime),
        };
        db.RefreshTokens.Add(refresh);

        var access = accessTokenService.Issue(user.Id, session.Id);
        await db.SaveChangesAsync(cancellationToken);

        return new IssuedSession(
            session.Id,
            access.Token,
            access.ExpiresAt,
            refreshToken,
            refresh.IdleExpiresAt);
    }

    public async Task RevokeAsync(
        Guid sessionId,
        Guid userId,
        string reason,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var session = await db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);
        if (session is null)
        {
            return;
        }

        if (session.RevokedAt is null)
        {
            session.RevokedAt = now;
            session.RevokedReason = reason;
        }

        var activeTokens = await db.RefreshTokens
            .Where(t => t.SessionId == session.Id && t.RotatedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in activeTokens)
        {
            token.RotatedAt = now;
        }

        securityEvents.Record(SecurityEventType.SessionRevoked, userId, ip, userAgent, correlationId, session.DeviceId);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeAllForUserAsync(
        Guid userId,
        Guid? exceptSessionId,
        string reason,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sessions = await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null
                && (exceptSessionId == null || s.Id != exceptSessionId))
            .ToListAsync(cancellationToken);
        if (sessions.Count == 0)
        {
            return;
        }

        var sessionIds = sessions.Select(s => s.Id).ToList();
        var tokens = await db.RefreshTokens
            .Where(t => sessionIds.Contains(t.SessionId) && t.RotatedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.RotatedAt = now;
        }

        foreach (var session in sessions)
        {
            session.RevokedAt = now;
            session.RevokedReason = reason;
        }

        securityEvents.Record(SecurityEventType.AllSessionsRevoked, userId, ip, userAgent, correlationId);
        await db.SaveChangesAsync(cancellationToken);
    }
}

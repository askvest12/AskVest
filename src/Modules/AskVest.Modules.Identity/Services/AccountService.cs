using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Identity.Services;

public sealed record AccountProfile(
    Guid Id,
    string Email,
    bool EmailVerified,
    string? MaskedPhone,
    bool PhoneVerified,
    bool HasPin,
    string Status,
    IReadOnlyList<string> Roles,
    DateTimeOffset? DeletionScheduledFor);

public sealed record SessionListItem(
    Guid Id,
    Guid DeviceId,
    string? InstallationId,
    string? DeviceName,
    string? Platform,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset AbsoluteExpiresAt,
    bool IsCurrent,
    string? CoarseIp,
    string? UserAgent);

public sealed record DeviceListItem(
    Guid Id,
    string InstallationId,
    string Platform,
    string DeviceName,
    string? AppVersion,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    bool Trusted,
    bool Current);

public sealed record SecurityEventListItem(
    Guid Id,
    string EventType,
    DateTimeOffset OccurredAt,
    string? CoarseIp,
    string? UserAgent,
    Guid? DeviceId,
    string? CorrelationId);

public sealed class AccountService(
    IdentityDbContext db,
    SessionService sessions,
    SecurityEventRecorder securityEvents,
    TimeProvider timeProvider)
{
    private static readonly Error _notFound = new(
        ErrorCodes.ResourceNotFound,
        "The resource was not found.",
        ErrorType.NotFound);

    public async Task<Result<AccountProfile>> MeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<AccountProfile>(_notFound);
        }

        var roles = await db.UserRoles
            .Where(r => r.UserId == userId)
            .Select(r => r.RoleName)
            .ToListAsync(cancellationToken);

        return Result<AccountProfile>.Success(new AccountProfile(
            user.Id,
            user.NormalizedEmail,
            user.EmailVerifiedAt is not null,
            PrivacyMasking.MaskPhone(user.PhoneE164),
            user.PhoneVerifiedAt is not null,
            user.PinHash is not null,
            user.Status.ToString(),
            roles,
            user.DeletionScheduledFor));
    }

    public async Task<IReadOnlyList<SessionListItem>> ListSessionsAsync(
        Guid userId, Guid currentSessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var rows = await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.AbsoluteExpiresAt > now)
            .OrderByDescending(s => s.LastUsedAt)
            .Join(db.Devices, s => s.DeviceId, d => d.Id, (s, d) => new { s, d })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new SessionListItem(
            row.s.Id,
            row.s.DeviceId,
            row.d.InstallationId,
            row.d.DeviceName,
            row.d.Platform,
            row.s.CreatedAt,
            row.s.LastUsedAt,
            row.s.AbsoluteExpiresAt,
            row.s.Id == currentSessionId,
            PrivacyMasking.CoarsenIp(row.s.CreationIp),
            row.s.UserAgent)).ToList();
    }

    public async Task<Result> RevokeSessionAsync(
        Guid userId,
        Guid sessionId,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var session = await db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null || session.UserId != userId)
        {
            return Result.Failure(_notFound);
        }

        await sessions.RevokeAsync(sessionId, userId, "user_revoked", ip, userAgent, correlationId, cancellationToken);
        return Result.Success();
    }

    public async Task<Result> RevokeOtherSessionsAsync(
        Guid userId,
        Guid currentSessionId,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        await sessions.RevokeAllForUserAsync(
            userId, currentSessionId, "user_revoked", ip, userAgent, correlationId, cancellationToken);
        return Result.Success();
    }

    public async Task<IReadOnlyList<DeviceListItem>> ListDevicesAsync(
        Guid userId, Guid currentSessionId, CancellationToken cancellationToken)
    {
        var currentDeviceId = await db.Sessions
            .Where(s => s.Id == currentSessionId && s.UserId == userId)
            .Select(s => (Guid?)s.DeviceId)
            .FirstOrDefaultAsync(cancellationToken);

        var devices = await db.Devices
            .Where(d => d.UserId == userId && d.RevokedAt == null)
            .OrderByDescending(d => d.LastSeenAt)
            .ToListAsync(cancellationToken);

        return devices.Select(d => new DeviceListItem(
            d.Id,
            d.InstallationId,
            d.Platform,
            d.DeviceName,
            d.AppVersion,
            d.FirstSeenAt,
            d.LastSeenAt,
            d.TrustedAt is not null,
            d.Id == currentDeviceId)).ToList();
    }

    public async Task<Result> RevokeDeviceAsync(
        Guid userId,
        Guid deviceId,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var device = await db.Devices
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null || device.UserId != userId)
        {
            return Result.Failure(_notFound);
        }

        var now = timeProvider.GetUtcNow();
        device.RevokedAt = now;

        var deviceSessions = await db.Sessions
            .Where(s => s.DeviceId == device.Id && s.RevokedAt == null)
            .ToListAsync(cancellationToken);
        var sessionIds = deviceSessions.Select(s => s.Id).ToList();
        var tokens = await db.RefreshTokens
            .Where(t => sessionIds.Contains(t.SessionId) && t.RotatedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.RotatedAt = now;
        }

        foreach (var session in deviceSessions)
        {
            session.RevokedAt = now;
            session.RevokedReason = "device_revoked";
        }

        securityEvents.Record(SecurityEventType.SessionRevoked, userId, ip, userAgent, correlationId, device.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<PageResult<SecurityEventListItem>> SecurityEventsAsync(
        Guid userId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var hasCursor = TryDecodeCursor(cursor, out var cursorAt, out var cursorId);
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != System.Data.ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, event_type, occurred_at, ip, user_agent, device_id, correlation_id
            FROM identity.security_events
            WHERE user_id = @userId
              AND (@hasCursor = FALSE
                   OR occurred_at < @cursorAt
                   OR (occurred_at = @cursorAt AND id < @cursorId::uuid))
            ORDER BY occurred_at DESC, id DESC
            LIMIT @limit
            """;
        AddParameter(command, "userId", userId);
        AddParameter(command, "hasCursor", hasCursor);
        AddParameter(command, "cursorAt", hasCursor ? cursorAt : (object)DBNull.Value);
        AddParameter(command, "cursorId", hasCursor ? cursorId : (object)DBNull.Value);
        AddParameter(command, "limit", limit + 1);

        var rows = new List<SecurityEventListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var occurredValue = await reader.GetFieldValueAsync<object>(2, cancellationToken);
            rows.Add(new SecurityEventListItem(
                await reader.GetFieldValueAsync<Guid>(0, cancellationToken),
                await reader.GetFieldValueAsync<string>(1, cancellationToken),
                occurredValue is DateTime occurred
                    ? new DateTimeOffset(DateTime.SpecifyKind(occurred, DateTimeKind.Utc))
                    : (DateTimeOffset)occurredValue,
                PrivacyMasking.CoarsenIp(await reader.IsDBNullAsync(3, cancellationToken) ? null : await reader.GetFieldValueAsync<string>(3, cancellationToken)),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : await reader.GetFieldValueAsync<string>(4, cancellationToken),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : await reader.GetFieldValueAsync<Guid>(5, cancellationToken),
                await reader.IsDBNullAsync(6, cancellationToken) ? null : await reader.GetFieldValueAsync<string>(6, cancellationToken)));
        }

        await reader.DisposeAsync();
        if (closeConnection)
        {
            await connection.CloseAsync();
        }

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        var nextCursor = hasMore && page.Count > 0
            ? CursorCodec.Encode($"{page[^1].OccurredAt:O}|{page[^1].Id}")
            : null;
        return new PageResult<SecurityEventListItem>(page, nextCursor, hasMore, limit);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool TryDecodeCursor(string? cursor, out DateTimeOffset occurredAt, out Guid id)
    {
        occurredAt = default;
        id = Guid.Empty;
        if (cursor is null || !CursorCodec.TryDecode(cursor, out var payload) || payload is null)
        {
            return false;
        }

        var separator = payload.IndexOf('|', StringComparison.Ordinal);
        return separator > 0
            && DateTimeOffset.TryParse(payload[..separator], out occurredAt)
            && Guid.TryParse(payload[(separator + 1)..], out id);
    }
}

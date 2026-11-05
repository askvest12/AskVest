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

public abstract record LoginOutcome;

public sealed record LoginAuthenticated(IssuedSession Session) : LoginOutcome;

public sealed record LoginDeviceVerificationRequired(Guid ChallengeId, DateTimeOffset ExpiresAt) : LoginOutcome;

public sealed record LoginAttempt(Result<LoginOutcome>? Outcome, Error? Failure, TimeSpan? RetryAfter)
{
    public static LoginAttempt Success(LoginOutcome outcome) => new(Result<LoginOutcome>.Success(outcome), null, null);

    public static LoginAttempt Failed(Error error) => new(null, error, null);

    public static LoginAttempt Throttled(Error error, TimeSpan retryAfter) => new(null, error, retryAfter);
}

public sealed class LoginService(
    IdentityDbContext db,
    IPasswordHasher passwordHasher,
    IOptions<IdentitySecretsOptions> secrets,
    ThrottleService throttle,
    OneTimeCodeService oneTimeCodes,
    SessionService sessions,
    SecurityEventRecorder securityEvents,
    IMessageChannel messageChannel,
    TimeProvider timeProvider)
{
    private static readonly Error InvalidCredentials = new(
        ErrorCodes.AuthInvalidCredentials,
        "The email or password is incorrect.",
        ErrorType.Unauthenticated);

    private readonly Lazy<Task<string>> _dummyHash = new(() =>
        passwordHasher.HashAsync("AskVestDummyPassword1!", secrets.Value.PasswordPepper!, CancellationToken.None));

    public async Task<LoginAttempt> LoginAsync(
        LoginRequest request,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Normalize(request.Email, 254);
        var normalized = email.IsSuccess ? email.Value : (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        var accountKey = normalized;
        var ipKey = ip ?? "unknown";

        var accountLock = await throttle.LockedForAsync(ThrottleKind.Account, accountKey, cancellationToken);
        var ipLock = await throttle.LockedForAsync(ThrottleKind.ClientIp, ipKey, cancellationToken);
        var lockFor = Max(accountLock, ipLock);
        if (lockFor is not null)
        {
            securityEvents.Record(SecurityEventType.LoginThrottled, ip: ip, userAgent: userAgent, correlationId: correlationId);
            await db.SaveChangesAsync(cancellationToken);
            return LoginAttempt.Throttled(
                new Error(ErrorCodes.RateLimitExceeded, "Too many requests.", ErrorType.RateLimited),
                lockFor.Value);
        }

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);

        var passwordValid = user is not null
            ? await passwordHasher.VerifyAsync(request.Password ?? string.Empty, secrets.Value.PasswordPepper!, user.PasswordHash, cancellationToken)
            : await DummyVerifyAsync(request.Password ?? string.Empty, cancellationToken);

        if (user is null || !passwordValid)
        {
            securityEvents.Record(SecurityEventType.LoginFailed, user?.Id, ip, userAgent, correlationId);
            await throttle.RecordFailureAsync(ThrottleKind.Account, accountKey, cancellationToken);
            await throttle.RecordFailureAsync(ThrottleKind.ClientIp, ipKey, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return LoginAttempt.Failed(InvalidCredentials);
        }

        if (user.Status == UserStatus.PendingVerification)
        {
            return LoginAttempt.Failed(new Error(
                ErrorCodes.AuthEmailNotVerified,
                "The email address is not verified.",
                ErrorType.Unauthenticated));
        }

        if (user.Status == UserStatus.Disabled)
        {
            return LoginAttempt.Failed(new Error(
                ErrorCodes.AuthAccountDisabled,
                "The account is disabled.",
                ErrorType.Forbidden));
        }

        if (user.Status == UserStatus.DeletionRequested)
        {
            return LoginAttempt.Failed(new Error(
                ErrorCodes.AuthAccountPendingDeletion,
                "The account is pending deletion.",
                ErrorType.Forbidden));
        }

        if (user.Status != UserStatus.Active)
        {
            return LoginAttempt.Failed(InvalidCredentials);
        }

        await throttle.ResetAsync(ThrottleKind.Account, accountKey, cancellationToken);

        if (passwordHasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = await passwordHasher.HashAsync(request.Password!, secrets.Value.PasswordPepper!, cancellationToken);
            user.PasswordPepperVersion = secrets.Value.PasswordPepperVersion;
        }

        user.LastLoginAt = timeProvider.GetUtcNow();
        user.UpdatedAt = timeProvider.GetUtcNow();

        var device = await UpsertDeviceAsync(user, request.Device, cancellationToken);
        var hasTrustedDevice = await db.Devices.AnyAsync(
            d => d.UserId == user.Id && d.TrustedAt != null && d.RevokedAt == null,
            cancellationToken);

        if (!hasTrustedDevice)
        {
            device.TrustedAt = timeProvider.GetUtcNow();
            securityEvents.Record(SecurityEventType.DeviceTrusted, user.Id, ip, userAgent, correlationId, device.Id);
            var issued = await sessions.CreateAsync(user, device, ip, userAgent, cancellationToken);
            securityEvents.Record(SecurityEventType.LoginSucceeded, user.Id, ip, userAgent, correlationId, device.Id);
            await db.SaveChangesAsync(cancellationToken);
            return LoginAttempt.Success(new LoginAuthenticated(issued));
        }

        if (device.TrustedAt is null || device.RevokedAt is not null)
        {
            var issued = await oneTimeCodes.IssueAsync(
                user.Id, OneTimeCodePurpose.DeviceVerification, CodeDestinationType.Email, user.NormalizedEmail,
                device.Id, cancellationToken);
            messageChannel.TryEnqueue(new QueuedEmail(MessageTemplates.DeviceVerification(user.NormalizedEmail, issued.Code)));
            securityEvents.Record(SecurityEventType.DeviceVerificationRequested, user.Id, ip, userAgent, correlationId, device.Id);
            await db.SaveChangesAsync(cancellationToken);
            return LoginAttempt.Success(new LoginDeviceVerificationRequired(issued.Id, issued.ExpiresAt));
        }

        var session = await sessions.CreateAsync(user, device, ip, userAgent, cancellationToken);
        securityEvents.Record(SecurityEventType.LoginSucceeded, user.Id, ip, userAgent, correlationId, device.Id);
        await db.SaveChangesAsync(cancellationToken);
        return LoginAttempt.Success(new LoginAuthenticated(session));
    }

    private async Task<Device> UpsertDeviceAsync(
        UserAccount user,
        DeviceInfoRequest? info,
        CancellationToken cancellationToken)
    {
        var installationId = string.IsNullOrWhiteSpace(info?.InstallationId)
            ? $"unknown-{Guid.CreateVersion7():N}"
            : info.InstallationId.Trim();
        var now = timeProvider.GetUtcNow();

        var device = await db.Devices.FirstOrDefaultAsync(
            d => d.UserId == user.Id && d.InstallationId == installationId,
            cancellationToken);
        if (device is null)
        {
            device = new Device
            {
                Id = Guid.CreateVersion7(),
                UserId = user.Id,
                InstallationId = installationId,
                Platform = (info?.Platform ?? "unknown")[..Math.Min(32, (info?.Platform ?? "unknown").Length)],
                DeviceName = Truncate(info?.DeviceName ?? "Unknown device", 120),
                AppVersion = info?.AppVersion is null ? null : Truncate(info.AppVersion, 40),
                FirstSeenAt = now,
                LastSeenAt = now,
            };
            db.Devices.Add(device);
        }
        else
        {
            device.LastSeenAt = now;
            device.DeviceName = Truncate(info?.DeviceName ?? device.DeviceName, 120);
            device.AppVersion = info?.AppVersion is null ? device.AppVersion : Truncate(info.AppVersion, 40);
        }

        return device;
    }

    private async Task<bool> DummyVerifyAsync(string password, CancellationToken cancellationToken)
    {
        var hash = await _dummyHash.Value;
        return await passwordHasher.VerifyAsync(password, secrets.Value.PasswordPepper!, hash, cancellationToken);
    }

    private static TimeSpan? Max(TimeSpan? a, TimeSpan? b)
        => a is null ? b : b is null ? a : a > b ? a : b;

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}

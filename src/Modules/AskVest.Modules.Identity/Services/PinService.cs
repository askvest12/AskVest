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

public sealed record PinAttempt(Result? Outcome, Error? Failure, TimeSpan? RetryAfter)
{
    public static PinAttempt Done() => new(Result.Success(), null, null);

    public static PinAttempt Failed(Error error) => new(null, error, null);

    public static PinAttempt Locked(Error error, TimeSpan retryAfter) => new(null, error, retryAfter);
}

public sealed record PinVerification(bool Success, Error? Failure = null, TimeSpan? RetryAfter = null)
{
    public static readonly PinVerification Ok = new(true);
}

public sealed class PinService(
    IdentityDbContext db,
    IPinHasher pinHasher,
    IPasswordHasher passwordHasher,
    IOptions<IdentitySecretsOptions> secrets,
    IOptions<IdentityOptions> options,
    OneTimeCodeService oneTimeCodes,
    SecurityEventRecorder securityEvents,
    IMessageChannel messageChannel,
    TimeProvider timeProvider)
{
    private static readonly Error PinNotSet = new(
        ErrorCodes.AuthPinNotSet,
        "No PIN is set on this account.",
        ErrorType.BusinessRule);

    private static readonly Error PinInvalid = new(
        ErrorCodes.AuthPinInvalid,
        "The PIN is incorrect or does not meet the requirements.",
        ErrorType.BusinessRule);

    private static readonly Error InvalidCredentials = new(
        ErrorCodes.AuthInvalidCredentials,
        "The email or password is incorrect.",
        ErrorType.Unauthenticated);

    private static readonly Error InvalidCode = new(
        ErrorCodes.AuthInvalidCode,
        "The code is invalid or has expired.",
        ErrorType.BusinessRule);

    private readonly IdentityOptions _options = options.Value;

    public async Task<Result> SetAsync(
        Guid userId,
        SetPinRequest request,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var user = await LoadUserAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(SessionGone());
        }

        if (user.PinHash is not null)
        {
            return Result.Failure(new Error(
                ErrorCodes.BusinessRuleViolated,
                "A PIN is already set. Use the change or reset flow.",
                ErrorType.BusinessRule));
        }

        var passwordValid = await passwordHasher.VerifyAsync(
            request.Password ?? string.Empty,
            secrets.Value.PasswordPepper!,
            user.PasswordHash,
            cancellationToken);
        if (!passwordValid)
        {
            securityEvents.Record(SecurityEventType.LoginFailed, user.Id, ip, userAgent, correlationId);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Failure(InvalidCredentials);
        }

        var pinError = ValidateNewPin(request.Pin);
        if (pinError is not null)
        {
            return Result.Failure(pinError);
        }

        user.PinHash = await pinHasher.HashAsync(request.Pin!, secrets.Value.PasswordPepper!, cancellationToken);
        user.PinSetAt = timeProvider.GetUtcNow();
        securityEvents.Record(SecurityEventType.PinSet, user.Id, ip, userAgent, correlationId);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<PinAttempt> ChangeAsync(
        Guid userId,
        ChangePinRequest request,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var user = await LoadUserAsync(userId, cancellationToken);
        if (user is null)
        {
            return PinAttempt.Failed(SessionGone());
        }

        var verification = await VerifyPinAsync(user, request.CurrentPin, ip, userAgent, correlationId, cancellationToken);
        if (!verification.Success)
        {
            return verification.RetryAfter is { } retryAfter
                ? PinAttempt.Locked(verification.Failure!, retryAfter)
                : PinAttempt.Failed(verification.Failure!);
        }

        var pinError = ValidateNewPin(request.Pin);
        if (pinError is not null)
        {
            return PinAttempt.Failed(pinError);
        }

        user.PinHash = await pinHasher.HashAsync(request.Pin!, secrets.Value.PasswordPepper!, cancellationToken);
        user.PinSetAt = timeProvider.GetUtcNow();
        securityEvents.Record(SecurityEventType.PinChanged, user.Id, ip, userAgent, correlationId);
        await db.SaveChangesAsync(cancellationToken);
        return PinAttempt.Done();
    }

    public async Task<Result> RequestResetAsync(
        Guid userId,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var user = await LoadUserAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(SessionGone());
        }

        if (user.PinHash is null)
        {
            return Result.Failure(PinNotSet);
        }

        if (await oneTimeCodes.CanSendAsync(
                user.Id, OneTimeCodePurpose.PinReset, user.NormalizedEmail, cancellationToken))
        {
            var issued = await oneTimeCodes.IssueAsync(
                user.Id, OneTimeCodePurpose.PinReset, CodeDestinationType.Email, user.NormalizedEmail,
                cancellationToken: cancellationToken);
            messageChannel.TryEnqueue(new QueuedEmail(MessageTemplates.PinReset(user.NormalizedEmail, issued.Code)));
            securityEvents.Record(SecurityEventType.PinReset, user.Id, ip, userAgent, correlationId);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    public async Task<Result> ResetAsync(
        Guid userId,
        ResetPinRequest request,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var user = await LoadUserAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(SessionGone());
        }

        var passwordValid = await passwordHasher.VerifyAsync(
            request.Password ?? string.Empty,
            secrets.Value.PasswordPepper!,
            user.PasswordHash,
            cancellationToken);
        if (!passwordValid)
        {
            securityEvents.Record(SecurityEventType.LoginFailed, user.Id, ip, userAgent, correlationId);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Failure(InvalidCredentials);
        }

        var verified = await oneTimeCodes.TryVerifyAsync(
            user.Id, OneTimeCodePurpose.PinReset, request.Code ?? string.Empty, cancellationToken);
        if (!verified)
        {
            return Result.Failure(InvalidCode);
        }

        var pinError = ValidateNewPin(request.Pin);
        if (pinError is not null)
        {
            return Result.Failure(pinError);
        }

        user.PinHash = await pinHasher.HashAsync(request.Pin!, secrets.Value.PasswordPepper!, cancellationToken);
        user.PinSetAt = timeProvider.GetUtcNow();
        user.PinFailedCount = 0;
        user.PinLockedUntil = null;
        user.PinLockRepeatCount = 0;

        var now = timeProvider.GetUtcNow();
        var grants = await db.StepUpGrants
            .Where(g => g.UserId == user.Id && g.ConsumedAt == null && g.ExpiresAt > now)
            .ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            grant.ExpiresAt = now;
        }

        securityEvents.Record(SecurityEventType.PinReset, user.Id, ip, userAgent, correlationId);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<PinVerification> VerifyPinAsync(
        UserAccount user,
        string? pin,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (user.PinLockedUntil is { } lockedUntil && lockedUntil > now)
        {
            return new PinVerification(
                false,
                new Error(ErrorCodes.AuthPinLocked, "The PIN is locked.", ErrorType.RateLimited),
                lockedUntil - now);
        }

        if (user.PinHash is null)
        {
            return new PinVerification(false, PinNotSet);
        }

        if (user.PinLockedUntil is not null)
        {
            user.PinLockedUntil = null;
            user.PinFailedCount = 0;
        }

        var valid = await pinHasher.VerifyAsync(
            pin ?? string.Empty, secrets.Value.PasswordPepper!, user.PinHash, cancellationToken);
        if (valid)
        {
            user.PinFailedCount = 0;
            user.PinLockRepeatCount = 0;
            return PinVerification.Ok;
        }

        user.PinFailedCount++;
        securityEvents.Record(SecurityEventType.PinFailed, user.Id, ip, userAgent, correlationId);
        if (user.PinFailedCount >= _options.PinMaxFailedAttempts)
        {
            var duration = _options.PinLockDuration * Math.Pow(2, user.PinLockRepeatCount);
            var capped = duration > _options.PinLockMaxDuration ? _options.PinLockMaxDuration : duration;
            user.PinLockRepeatCount++;
            user.PinLockedUntil = now.Add(capped);
            securityEvents.Record(SecurityEventType.PinLocked, user.Id, ip, userAgent, correlationId);
            await db.SaveChangesAsync(cancellationToken);
            return new PinVerification(
                false,
                new Error(ErrorCodes.AuthPinLocked, "The PIN is locked.", ErrorType.RateLimited),
                capped);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new PinVerification(false, PinInvalid);
    }

    private async Task<UserAccount?> LoadUserAsync(Guid userId, CancellationToken cancellationToken)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    private static Error? ValidateNewPin(string? pin)
        => PinRules.IsWellFormed(pin) && !PinRules.IsGuessable(pin!)
            ? null
            : new Error(ErrorCodes.AuthPinInvalid, "The PIN does not meet the requirements.", ErrorType.BusinessRule);

    private static Error SessionGone() => new(
        ErrorCodes.AuthSessionRevoked,
        "The session is no longer valid.",
        ErrorType.Unauthenticated);
}

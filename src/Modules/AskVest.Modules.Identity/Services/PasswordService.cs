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

public sealed class PasswordService(
    IdentityDbContext db,
    IPasswordHasher passwordHasher,
    IPasswordPolicy passwordPolicy,
    IOptions<IdentitySecretsOptions> secrets,
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

    private static readonly Error InvalidCode = new(
        ErrorCodes.AuthInvalidCode,
        "The code is invalid or has expired.",
        ErrorType.BusinessRule);

    private readonly Lazy<Task<string>> _dummyHash = new(() =>
        passwordHasher.HashAsync("AskVestDummyPassword1!", secrets.Value.PasswordPepper!, CancellationToken.None));

    public async Task<Result> ForgotAsync(
        string? rawEmail,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Normalize(rawEmail, 254);
        var normalized = email.IsSuccess ? email.Value : (rawEmail ?? string.Empty).Trim().ToLowerInvariant();

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);

        if (user is null || user.Status != UserStatus.Active)
        {
            var hash = await _dummyHash.Value;
            await passwordHasher.VerifyAsync("dummy", secrets.Value.PasswordPepper!, hash, cancellationToken);
            return Result.Success();
        }

        if (await oneTimeCodes.CanSendAsync(
                user.Id, OneTimeCodePurpose.PasswordReset, normalized, cancellationToken))
        {
            var issued = await oneTimeCodes.IssueAsync(
                user.Id, OneTimeCodePurpose.PasswordReset, CodeDestinationType.Email, normalized,
                cancellationToken: cancellationToken);
            messageChannel.TryEnqueue(new QueuedEmail(MessageTemplates.PasswordReset(normalized, issued.Code)));
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    public async Task<Result> ResetAsync(
        ResetPasswordRequest request,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var policy = await passwordPolicy.ValidateAsync(request.Password, request.Email, cancellationToken);
        if (policy.IsFailure)
        {
            return policy;
        }

        var email = EmailNormalizer.Normalize(request.Email, 254);
        var normalized = email.IsSuccess ? email.Value : (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);

        var verified = user is not null && await oneTimeCodes.TryVerifyAsync(
            user.Id, OneTimeCodePurpose.PasswordReset, request.Code!, cancellationToken);
        if (!verified)
        {
            return Result.Failure(InvalidCode);
        }

        user!.PasswordHash = await passwordHasher.HashAsync(
            request.Password!, secrets.Value.PasswordPepper!, cancellationToken);
        user.PasswordPepperVersion = secrets.Value.PasswordPepperVersion;
        user.UpdatedAt = timeProvider.GetUtcNow();

        await sessions.RevokeAllForUserAsync(
            user.Id, null, "password_reset", ip, userAgent, correlationId, cancellationToken);
        securityEvents.Record(SecurityEventType.PasswordReset, user.Id, ip, userAgent, correlationId);
        messageChannel.TryEnqueue(new QueuedEmail(MessageTemplates.PasswordResetCompleted(normalized)));
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> ChangeAsync(
        Guid userId,
        Guid currentSessionId,
        ChangePasswordRequest request,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(new Error(
                ErrorCodes.AuthSessionRevoked,
                "The session is no longer valid.",
                ErrorType.Unauthenticated));
        }

        var passwordValid = await passwordHasher.VerifyAsync(
            request.CurrentPassword ?? string.Empty,
            secrets.Value.PasswordPepper!,
            user.PasswordHash,
            cancellationToken);
        if (!passwordValid)
        {
            securityEvents.Record(SecurityEventType.LoginFailed, user.Id, ip, userAgent, correlationId);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Failure(InvalidCredentials);
        }

        var policy = await passwordPolicy.ValidateAsync(request.Password, user.NormalizedEmail, cancellationToken);
        if (policy.IsFailure)
        {
            return policy;
        }

        user.PasswordHash = await passwordHasher.HashAsync(
            request.Password!, secrets.Value.PasswordPepper!, cancellationToken);
        user.PasswordPepperVersion = secrets.Value.PasswordPepperVersion;
        user.UpdatedAt = timeProvider.GetUtcNow();

        await sessions.RevokeAllForUserAsync(
            user.Id, currentSessionId, "password_changed", ip, userAgent, correlationId, cancellationToken);
        securityEvents.Record(SecurityEventType.PasswordChanged, user.Id, ip, userAgent, correlationId);
        messageChannel.TryEnqueue(new QueuedEmail(MessageTemplates.PasswordChanged(user.NormalizedEmail)));
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

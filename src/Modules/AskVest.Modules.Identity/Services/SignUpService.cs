using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Messaging;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Services;

public sealed class SignUpService(
    IdentityDbContext db,
    IPasswordHasher passwordHasher,
    IPasswordPolicy passwordPolicy,
    IOptions<IdentitySecretsOptions> secrets,
    OneTimeCodeService oneTimeCodes,
    SecurityEventRecorder securityEvents,
    IMessageChannel messageChannel,
    TimeProvider timeProvider)
{
    private static readonly Error InvalidEmail = new(
        ErrorCodes.ValidationFailed,
        "The email address is invalid.",
        ErrorType.Validation,
        [new ValidationFailure("email", "The email address is invalid.")]);

    public Task<Result> SignUpAsync(
        string? rawEmail,
        string? password,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
        => SignUpAsync(rawEmail, password, ip, userAgent, correlationId, retried: false, cancellationToken);

    private async Task<Result> SignUpAsync(
        string? rawEmail,
        string? password,
        string? ip,
        string? userAgent,
        string? correlationId,
        bool retried,
        CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Normalize(rawEmail, 254);
        if (email.IsFailure)
        {
            return Result.Failure(InvalidEmail);
        }

        var normalized = email.Value;
        var policy = await passwordPolicy.ValidateAsync(password, normalized, cancellationToken);
        if (policy.IsFailure)
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "The request is not valid.",
                ErrorType.Validation,
                [new ValidationFailure("password", policy.Error!.Message)]));
        }

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);

        if (user is null)
        {
            user = new UserAccount
            {
                Id = Guid.CreateVersion7(),
                NormalizedEmail = normalized,
                PasswordHash = await passwordHasher.HashAsync(password!, secrets.Value.PasswordPepper!, cancellationToken),
                PasswordPepperVersion = secrets.Value.PasswordPepperVersion,
                Status = UserStatus.PendingVerification,
                CreatedAt = timeProvider.GetUtcNow(),
                UpdatedAt = timeProvider.GetUtcNow(),
            };
            db.Users.Add(user);
            var signUpEvent = securityEvents.Record(SecurityEventType.SignUp, user.Id, ip, userAgent, correlationId);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.Entry(user).State = EntityState.Detached;
                db.Entry(signUpEvent).State = EntityState.Detached;
                var winner = await db.Users
                    .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
                if (winner is null)
                {
                    return retried
                        ? Result.Success()
                        : await SignUpAsync(rawEmail, password, ip, userAgent, correlationId, retried: true, cancellationToken);
                }

                if (winner.Status == UserStatus.PendingVerification)
                {
                    securityEvents.Record(SecurityEventType.SignUp, winner.Id, ip, userAgent, correlationId);
                    await TryRefreshPendingAsync(winner, normalized, password!, cancellationToken);
                }
                else
                {
                    await RecordExistingAccountNoticeAsync(winner, ip, userAgent, correlationId, cancellationToken);
                }

                return Result.Success();
            }

            await IssueVerificationCodeAsync(user, normalized, cancellationToken);
        }
        else if (user.Status == UserStatus.PendingVerification)
        {
            securityEvents.Record(SecurityEventType.SignUp, user.Id, ip, userAgent, correlationId);
            await TryRefreshPendingAsync(user, normalized, password!, cancellationToken);
        }
        else
        {
            await RecordExistingAccountNoticeAsync(user, ip, userAgent, correlationId, cancellationToken);
        }

        return Result.Success();
    }

    private async Task RecordExistingAccountNoticeAsync(
        UserAccount user,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        securityEvents.Record(SecurityEventType.SignUp, user.Id, ip, userAgent, correlationId, metadata: "{\"reason\":\"existing_account\"}");
        await db.SaveChangesAsync(cancellationToken);
        messageChannel.TryEnqueue(new QueuedEmail(MessageTemplates.SignUpAttemptOnExistingAccount(user.NormalizedEmail)));
    }

    private async Task TryRefreshPendingAsync(
        UserAccount user,
        string normalizedEmail,
        string password,
        CancellationToken cancellationToken)
    {
        try
        {
            user.PasswordHash = await passwordHasher.HashAsync(password, secrets.Value.PasswordPepper!, cancellationToken);
            user.PasswordPepperVersion = secrets.Value.PasswordPepperVersion;
            user.UpdatedAt = timeProvider.GetUtcNow();
            await IssueVerificationCodeAsync(user, normalizedEmail, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent sign-up for the same email refreshed the account first;
            // the uniform response is still correct.
        }
    }

    private async Task IssueVerificationCodeAsync(
        UserAccount user,
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        if (await oneTimeCodes.CanSendAsync(user.Id, OneTimeCodePurpose.EmailVerification, normalizedEmail, cancellationToken))
        {
            var issued = await oneTimeCodes.IssueAsync(
                user.Id, OneTimeCodePurpose.EmailVerification, CodeDestinationType.Email, normalizedEmail,
                cancellationToken: cancellationToken);
            messageChannel.TryEnqueue(new QueuedEmail(MessageTemplates.EmailVerification(normalizedEmail, issued.Code)));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

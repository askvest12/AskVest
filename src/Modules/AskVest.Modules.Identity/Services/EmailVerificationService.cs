using AskVest.SharedKernel;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Messaging;
using AskVest.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Identity.Services;

public sealed class EmailVerificationService(
    IdentityDbContext db,
    OneTimeCodeService oneTimeCodes,
    SecurityEventRecorder securityEvents,
    IMessageChannel messageChannel,
    TimeProvider timeProvider)
{
    private static readonly Error InvalidCode = new(
        SharedKernel.ErrorCodes.AuthInvalidCode,
        "The code is invalid, expired or already used.",
        SharedKernel.ErrorType.Validation);

    public async Task<Result> VerifyAsync(
        string? rawEmail,
        string? code,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Normalize(rawEmail, 254);
        if (email.IsFailure || string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure(InvalidCode);
        }

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.NormalizedEmail == email.Value && u.Status == UserStatus.PendingVerification,
            cancellationToken);
        if (user is null)
        {
            return Result.Failure(InvalidCode);
        }

        var verified = await oneTimeCodes.TryVerifyAsync(
            user.Id, OneTimeCodePurpose.EmailVerification, code.Trim(), cancellationToken);
        if (!verified)
        {
            return Result.Failure(InvalidCode);
        }

        user.EmailVerifiedAt = timeProvider.GetUtcNow();
        user.Status = UserStatus.Active;
        user.UpdatedAt = timeProvider.GetUtcNow();
        securityEvents.Record(SecurityEventType.EmailVerified, user.Id, ip, userAgent, correlationId);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> ResendAsync(
        string? rawEmail,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Normalize(rawEmail, 254);
        if (email.IsFailure)
        {
            return Result.Success();
        }

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.NormalizedEmail == email.Value && u.Status == UserStatus.PendingVerification,
            cancellationToken);
        if (user is null)
        {
            return Result.Success();
        }

        if (await oneTimeCodes.CanSendAsync(user.Id, OneTimeCodePurpose.EmailVerification, email.Value, cancellationToken))
        {
            var issued = await oneTimeCodes.IssueAsync(
                user.Id, OneTimeCodePurpose.EmailVerification, CodeDestinationType.Email, email.Value, cancellationToken: cancellationToken);
            messageChannel.TryEnqueue(new QueuedEmail(MessageTemplates.EmailVerification(email.Value, issued.Code)));
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}

using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Messaging;
using AskVest.Modules.Identity.Persistence;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Services;

public sealed class PhoneService(
    IdentityDbContext db,
    OneTimeCodeService oneTimeCodes,
    IMessageChannel messageChannel,
    SecurityEventRecorder securityEvents,
    IOptions<IdentityOptions> options,
    TimeProvider timeProvider)
{
    private static readonly Error InvalidCode = new(
        ErrorCodes.AuthInvalidCode,
        "The code is invalid or has expired.",
        ErrorType.BusinessRule);

    private static readonly Error InvalidPhone = new(
        ErrorCodes.ValidationFailed,
        "The phone number is invalid.",
        ErrorType.Validation);

    private readonly IdentityOptions _options = options.Value;

    public async Task<Result> AddAsync(
        Guid userId,
        AddPhoneRequest request,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(SessionGone());
        }

        var normalized = PhoneNormalizer.Normalize(request.Phone, _options.DefaultPhoneRegion);
        if (normalized.IsFailure)
        {
            return Result.Failure(InvalidPhone);
        }

        var e164 = normalized.Value;
        if (await oneTimeCodes.CanSendAsync(
                user.Id, OneTimeCodePurpose.PhoneVerification, e164, cancellationToken))
        {
            var issued = await oneTimeCodes.IssueAsync(
                user.Id, OneTimeCodePurpose.PhoneVerification, CodeDestinationType.Phone, e164,
                cancellationToken: cancellationToken);
            messageChannel.TryEnqueue(new QueuedSms(MessageTemplates.PhoneVerification(e164, issued.Code)));
            securityEvents.Record(SecurityEventType.PhoneAdded, user.Id, ip, userAgent, correlationId);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    public async Task<Result> VerifyAsync(
        Guid userId,
        VerifyPhoneRequest request,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(SessionGone());
        }

        var now = timeProvider.GetUtcNow();
        var pending = await db.OneTimeCodes
            .Where(c => c.UserId == user.Id
                && c.Purpose == OneTimeCodePurpose.PhoneVerification
                && c.ConsumedAt == null
                && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (pending is null)
        {
            return Result.Failure(InvalidCode);
        }

        var verified = await oneTimeCodes.TryVerifyCodeAsync(
            pending.Id, user.Id, OneTimeCodePurpose.PhoneVerification,
            request.Code ?? string.Empty, cancellationToken);
        if (!verified)
        {
            return Result.Failure(InvalidCode);
        }

        var e164 = pending.Destination;
        var taken = await db.Users.AnyAsync(
            u => u.Id != user.Id && u.PhoneE164 == e164 && u.PhoneVerifiedAt != null,
            cancellationToken);
        if (taken)
        {
            return Result.Failure(InvalidCode);
        }

        user.PhoneE164 = e164;
        user.PhoneVerifiedAt = now;
        securityEvents.Record(SecurityEventType.PhoneVerified, user.Id, ip, userAgent, correlationId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result.Failure(InvalidCode);
        }

        return Result.Success();
    }

    private static Error SessionGone() => new(
        ErrorCodes.AuthSessionRevoked,
        "The session is no longer valid.",
        ErrorType.Unauthenticated);
}

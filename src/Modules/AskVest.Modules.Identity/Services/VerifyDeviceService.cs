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

public sealed class VerifyDeviceService(
    IdentityDbContext db,
    OneTimeCodeService oneTimeCodes,
    SessionService sessions,
    SecurityEventRecorder securityEvents,
    IMessageChannel messageChannel,
    TimeProvider timeProvider)
{
    private static readonly Error InvalidCode = new(
        ErrorCodes.AuthInvalidCode,
        "The code is invalid, expired or already used.",
        ErrorType.Validation);

    public async Task<LoginAttempt> VerifyAsync(
        VerifyDeviceRequest request,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (request.ChallengeId is null || string.IsNullOrWhiteSpace(request.Code))
        {
            return LoginAttempt.Failed(InvalidCode);
        }

        var code = await db.OneTimeCodes
            .FirstOrDefaultAsync(c => c.Id == request.ChallengeId.Value
                && c.Purpose == OneTimeCodePurpose.DeviceVerification
                && c.ConsumedAt == null,
                cancellationToken);
        if (code is null)
        {
            return LoginAttempt.Failed(InvalidCode);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == code.UserId, cancellationToken);
        var device = code.DeviceId is null
            ? null
            : await db.Devices.FirstOrDefaultAsync(d => d.Id == code.DeviceId.Value, cancellationToken);
        if (user is null || device is null || user.Status != UserStatus.Active)
        {
            return LoginAttempt.Failed(InvalidCode);
        }

        var verified = await oneTimeCodes.TryVerifyCodeAsync(
            code.Id, user.Id, OneTimeCodePurpose.DeviceVerification, request.Code.Trim(), cancellationToken);
        if (!verified)
        {
            securityEvents.Record(SecurityEventType.LoginFailed, user.Id, ip, userAgent, correlationId, device.Id);
            await db.SaveChangesAsync(cancellationToken);
            return LoginAttempt.Failed(InvalidCode);
        }

        device.TrustedAt = timeProvider.GetUtcNow();
        securityEvents.Record(SecurityEventType.DeviceTrusted, user.Id, ip, userAgent, correlationId, device.Id);

        var issued = await sessions.CreateAsync(user, device, ip, userAgent, cancellationToken);
        securityEvents.Record(SecurityEventType.LoginSucceeded, user.Id, ip, userAgent, correlationId, device.Id);
        await db.SaveChangesAsync(cancellationToken);

        var deviceSummary = $"{device.DeviceName} ({device.Platform})";
        messageChannel.TryEnqueue(new QueuedEmail(MessageTemplates.NewDeviceSignIn(user.NormalizedEmail, deviceSummary)));

        return LoginAttempt.Success(new LoginAuthenticated(issued));
    }
}

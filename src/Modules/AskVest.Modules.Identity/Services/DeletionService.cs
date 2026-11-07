using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Messaging;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using AskVest.Modules.Identity.Services;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Services;

public sealed class DeletionService(
    IdentityDbContext db,
    IPasswordHasher passwordHasher,
    IOptions<IdentitySecretsOptions> secrets,
    IStepUpAuthorizer stepUp,
    SessionService sessions,
    SecurityEventRecorder securityEvents,
    IMessageChannel messageChannel,
    IEnumerable<IDeletionBlocker> deletionBlockers,
    IOptions<IdentityOptions> options,
    TimeProvider timeProvider)
{
    private static readonly Error _invalidCredentials = new(
        ErrorCodes.AuthInvalidCredentials,
        "The credentials are not valid.",
        ErrorType.Unauthenticated);

    private static readonly Error _notPending = new(
        ErrorCodes.ResourceNotFound,
        "There is no pending deletion for this account.",
        ErrorType.NotFound);

    public async Task<Result> RequestAsync(
        Guid userId,
        Guid currentSessionId,
        string? password,
        string? stepUpToken,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || user.Status == UserStatus.Anonymized)
        {
            return Result.Failure(new Error(
                ErrorCodes.AuthSessionRevoked,
                "The session is no longer valid.",
                ErrorType.Unauthenticated));
        }

        if (user.Status == UserStatus.DeletionRequested)
        {
            return Result.Success();
        }

        if (user.Status != UserStatus.Active)
        {
            return Result.Failure(_invalidCredentials);
        }

        var passwordValid = await passwordHasher.VerifyAsync(
            password ?? string.Empty,
            secrets.Value.PasswordPepper!,
            user.PasswordHash,
            cancellationToken);
        if (!passwordValid)
        {
            securityEvents.Record(SecurityEventType.LoginFailed, user.Id, ip, userAgent, correlationId);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Failure(_invalidCredentials);
        }

        var consumed = await stepUp.ConsumeAsync(
            userId, currentSessionId, StepUpPurposes.DeleteAccount, null, stepUpToken ?? string.Empty, cancellationToken);
        if (consumed.IsFailure)
        {
            return consumed;
        }

        var reasons = new List<string>();
        foreach (var blocker in deletionBlockers)
        {
            reasons.AddRange(await blocker.GetBlockReasonsAsync(userId, cancellationToken));
        }

        if (reasons.Count > 0)
        {
            return Result.Failure(new Error(
                ErrorCodes.AccountDeletionBlocked,
                "Account deletion is currently blocked.",
                ErrorType.BusinessRule,
                reasons.Select(r => new ValidationFailure("deletion", r)).ToList()));
        }

        var now = timeProvider.GetUtcNow();
        user.Status = UserStatus.DeletionRequested;
        user.DeletionRequestedAt = now;
        user.DeletionScheduledFor = now + options.Value.DeletionCoolingOff;
        user.UpdatedAt = now;

        await sessions.RevokeAllForUserAsync(
            user.Id, currentSessionId, "deletion_requested", ip, userAgent, correlationId, cancellationToken);
        securityEvents.Record(SecurityEventType.DeletionRequested, user.Id, ip, userAgent, correlationId);
        messageChannel.TryEnqueue(new QueuedEmail(
            MessageTemplates.DeletionRequested(user.NormalizedEmail, (int)options.Value.DeletionCoolingOff.TotalDays)));
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> CancelAsync(
        Guid userId,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || user.Status != UserStatus.DeletionRequested)
        {
            return Result.Failure(_notPending);
        }

        user.Status = UserStatus.Active;
        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.UpdatedAt = timeProvider.GetUtcNow();

        securityEvents.Record(SecurityEventType.DeletionCancelled, user.Id, ip, userAgent, correlationId);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

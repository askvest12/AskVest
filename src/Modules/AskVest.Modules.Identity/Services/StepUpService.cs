using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Services;

public sealed record StepUpGrantIssued(string Token, DateTimeOffset ExpiresAt);

public sealed record StepUpIssueResult(StepUpGrantIssued? Grant, Error? Failure, TimeSpan? RetryAfter)
{
    public static StepUpIssueResult Issued(StepUpGrantIssued grant) => new(grant, null, null);

    public static StepUpIssueResult Failed(Error error) => new(null, error, null);

    public static StepUpIssueResult Locked(Error error, TimeSpan retryAfter) => new(null, error, retryAfter);
}

public sealed class StepUpService(
    IdentityDbContext db,
    PinService pinService,
    ISecureRandom secureRandom,
    IKeyedHasher keyedHasher,
    SecurityEventRecorder securityEvents,
    IOptions<IdentityOptions> options,
    TimeProvider timeProvider) : IStepUpAuthorizer
{
    private static readonly Error _stepUpInvalid = new(
        ErrorCodes.AuthStepUpInvalid,
        "The step-up grant is invalid or has expired.",
        ErrorType.Forbidden);

    private readonly IdentityOptions _options = options.Value;

    public async Task<StepUpIssueResult> IssueAsync(
        Guid userId,
        Guid sessionId,
        StepUpRequest request,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (!StepUpPurposes.IsKnown(request.Purpose))
        {
            return StepUpIssueResult.Failed(_stepUpInvalid);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return StepUpIssueResult.Failed(new Error(
                ErrorCodes.AuthSessionRevoked,
                "The session is no longer valid.",
                ErrorType.Unauthenticated));
        }

        var verification = await pinService.VerifyPinAsync(
            user, request.Pin ?? string.Empty, ip, userAgent, correlationId, cancellationToken);
        if (!verification.Success)
        {
            return verification.RetryAfter is { } retryAfter
                ? StepUpIssueResult.Locked(verification.Failure!, retryAfter)
                : StepUpIssueResult.Failed(verification.Failure!);
        }

        var now = timeProvider.GetUtcNow();
        var token = secureRandom.NewToken(32);
        var grant = new StepUpGrant
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            SessionId = sessionId,
            Purpose = request.Purpose!,
            BindingHash = string.IsNullOrEmpty(request.Binding) ? null : keyedHasher.Sha256Hex(request.Binding),
            TokenHash = keyedHasher.Sha256Hex(token),
            ExpiresAt = now.Add(_options.StepUpGrantLifetime),
            CreatedAt = now,
        };
        db.StepUpGrants.Add(grant);
        securityEvents.Record(SecurityEventType.StepUpGranted, userId, ip, userAgent, correlationId);
        await db.SaveChangesAsync(cancellationToken);
        return StepUpIssueResult.Issued(new StepUpGrantIssued(token, grant.ExpiresAt));
    }

    public async Task<Result> ConsumeAsync(
        Guid userId,
        Guid sessionId,
        string purpose,
        string? binding,
        string token,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var tokenHash = keyedHasher.Sha256Hex(token);
        var bindingHash = string.IsNullOrEmpty(binding) ? null : keyedHasher.Sha256Hex(binding);

        var consumed = await db.Database.SqlQuery<Guid>($@"
            UPDATE identity.step_up_grants
            SET consumed_at = {now}
            WHERE id = (
                SELECT id FROM identity.step_up_grants
                WHERE token_hash = {tokenHash}
                  AND user_id = {userId}
                  AND session_id = {sessionId}
                  AND purpose = {purpose}
                  AND consumed_at IS NULL
                  AND expires_at > {now}
                  AND ({bindingHash}::text IS NULL OR binding_hash = {bindingHash})
                LIMIT 1
            )
            RETURNING id")
            .ToListAsync(cancellationToken);

        if (consumed.Count == 0)
        {
            return Result.Failure(_stepUpInvalid);
        }

        securityEvents.Record(SecurityEventType.StepUpConsumed, userId, null, null, null);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

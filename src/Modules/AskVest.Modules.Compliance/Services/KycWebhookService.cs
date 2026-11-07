using System.Security.Cryptography;
using System.Text;
using AskVest.Modules.Compliance.Configuration;
using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence;
using AskVest.Modules.Compliance.Persistence.Entities;
using AskVest.Modules.Compliance.Providers;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Compliance.Services;

internal enum KycWebhookResultKind
{
    Processed,
    Duplicate,
    UnknownProvider,
    StaleTimestamp,
    InvalidSignature,
    Malformed,
    ScreeningUnavailable,
}

/// <summary>
/// Handles provider decision webhooks: signature and timestamp checks, event
/// id deduplication, approval with screening and rejection. Personal data from
/// the payload is used for the screening call only and never persisted.
/// </summary>
internal sealed class KycWebhookService(
    ComplianceDbContext db,
    ProfileStore profileStore,
    IKycProvider kycProvider,
    IScreeningProvider screeningProvider,
    IOptions<ComplianceOptions> options,
    TimeProvider timeProvider,
    ILogger<KycWebhookService> logger)
{
    public const int MaxBodyBytes = 16 * 1024;

    public async Task<Result<KycWebhookResultKind>> ProcessAsync(
        string providerName,
        string rawBody,
        IReadOnlyDictionary<string, string?> headers,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var configuredProvider = options.Value.Kyc.Provider;
        if (!string.Equals(providerName, configuredProvider, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<KycWebhookResultKind>(new Error(
                ErrorCodes.RouteNotFound,
                "The route was not found.",
                ErrorType.NotFound));
        }

        var now = timeProvider.GetUtcNow();
        var parse = kycProvider.ParseWebhook(rawBody, headers, now);
        switch (parse.Status)
        {
            case KycWebhookParseStatus.MissingOrStaleTimestamp:
                return Result.Failure<KycWebhookResultKind>(new Error(
                    ErrorCodes.WebhookStaleTimestamp,
                    "The webhook timestamp is missing or stale.",
                    ErrorType.Validation));
            case KycWebhookParseStatus.InvalidSignature:
                profileStore.AddEvent(
                    null,
                    ComplianceEventType.WebhookRejected,
                    correlationId,
                    $"provider={providerName}");
                await db.SaveChangesAsync(cancellationToken);
                return Result.Failure<KycWebhookResultKind>(new Error(
                    ErrorCodes.WebhookInvalidSignature,
                    "The webhook signature is not valid.",
                    ErrorType.Unauthenticated));
            case KycWebhookParseStatus.Malformed:
                return Result.Failure<KycWebhookResultKind>(new Error(
                    ErrorCodes.ValidationFailed,
                    "The webhook payload is not valid.",
                    ErrorType.Validation));
        }

        var webhookEvent = parse.EventData!;
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken);

        var dedupe = new KycWebhookEvent
        {
            Id = Guid.CreateVersion7(),
            Provider = providerName,
            EventId = webhookEvent.EventId,
            ReceivedAt = now,
            Outcome = "received",
            BodyHash = bodyHash,
        };
        db.KycWebhookEvents.Add(dedupe);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            return Result.Success(KycWebhookResultKind.Duplicate);
        }

        var profile = await db.ComplianceProfiles
            .FirstOrDefaultAsync(
                p => p.ProviderReference == webhookEvent.ProviderReference
                    && p.KycProvider == providerName,
                cancellationToken);
        if (profile is null)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                "KYC webhook for unknown provider reference. Provider: {Provider}, EventId: {EventId}",
                    providerName,
                    webhookEvent.EventId);
            }

            dedupe.Outcome = "unmatched";
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success(KycWebhookResultKind.Processed);
        }

        if (webhookEvent.Outcome == KycWebhookOutcome.Approved)
        {
            var approval = await ApplyApprovalAsync(
                profile, webhookEvent, dedupe, providerName, correlationId, now, cancellationToken);
            if (approval.IsFailure)
            {
                return Result.Failure<KycWebhookResultKind>(approval.Error!);
            }
        }
        else
        {
            ApplyRejection(profile, webhookEvent, now);
            profileStore.AddEvent(
                profile.UserId,
                ComplianceEventType.KycRejected,
                correlationId,
                $"provider={providerName}");
            dedupe.Outcome = "rejected";
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(KycWebhookResultKind.Processed);
    }

    private async Task<Result> ApplyApprovalAsync(
        Persistence.Entities.ComplianceProfile profile,
        KycWebhookEventData webhookEvent,
        KycWebhookEvent dedupe,
        string providerName,
        string? correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookEvent.FullName) || webhookEvent.DateOfBirth is null)
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "The webhook payload is not valid.",
                ErrorType.Validation));
        }

        var verifiedCountry = webhookEvent.VerifiedCountry is { Length: 2 }
            && Domain.CountryCodes.IsValid(webhookEvent.VerifiedCountry)
                ? webhookEvent.VerifiedCountry
                : null;

        var screening = await screeningProvider.ScreenAsync(
            webhookEvent.FullName!,
            webhookEvent.DateOfBirth!.Value,
            verifiedCountry ?? profile.VerifiedCountry ?? profile.DeclaredCountry,
            cancellationToken);
        if (screening.IsFailure)
        {
            return Result.Failure(new Error(
                ErrorCodes.ServiceUnavailable,
                "The screening provider is unavailable.",
                ErrorType.Unavailable));
        }

        var decision = screening.Value;
        profile.KycStatus = KycStatus.Approved;
        profile.DecidedAt = now;
        profile.ExpiresAt = now.AddMonths(options.Value.Kyc.ApprovalValidityMonths);
        profile.VerifiedCountry = verifiedCountry;
        profile.RejectionCode = null;
        profile.ScreeningStatus = decision.Outcome;
        profile.ScreenedAt = now;
        profile.UpdatedAt = now;

        profileStore.AddEvent(
            profile.UserId,
            ComplianceEventType.KycApproved,
            correlationId,
            $"provider={providerName}");
        profileStore.AddEvent(
            profile.UserId,
            decision.Outcome == ScreeningStatus.Hit
                ? ComplianceEventType.ScreeningHit
                : ComplianceEventType.ScreeningClear,
            correlationId,
            $"provider={providerName}");

        dedupe.Outcome = decision.Outcome == ScreeningStatus.Hit ? "approved_screening_hit" : "approved";
        return Result.Success();
    }

    private static void ApplyRejection(
        Persistence.Entities.ComplianceProfile profile,
        KycWebhookEventData webhookEvent,
        DateTimeOffset now)
    {
        profile.KycStatus = KycStatus.Rejected;
        profile.DecidedAt = now;
        profile.RejectionCode = webhookEvent.RejectionCode is { Length: > 64 } code
            ? code[..64]
            : webhookEvent.RejectionCode;
        profile.UpdatedAt = now;
    }
}

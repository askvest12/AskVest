using System.Security.Cryptography;
using System.Text;
using AskVest.Modules.Funding.Persistence;
using AskVest.Modules.Funding.Persistence.Entities;
using AskVest.Modules.Funding.Providers;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// Signed funding provider webhooks. The HMAC signature is the credential:
/// unknown providers answer route.not_found, oversized bodies
/// request.too_large, stale timestamps webhook.stale_timestamp and bad
/// signatures webhook.invalid_signature. Dedupe uses the provider event id
/// inside the same transaction scope as the deposit processing.
/// </summary>
internal sealed class FundingWebhookService(
    FundingDbContext db,
    FundingProviderRegistry providers,
    FundingDepositService deposits,
    FundingPayoutProgressService payoutProgress,
    ITransactionScope scope,
    TimeProvider timeProvider)
{
    public async Task<Result> ProcessAsync(
        string providerKey,
        string rawBody,
        IReadOnlyDictionary<string, string?> headers,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (!providers.TryGet(providerKey, out var provider) || provider is null)
        {
            return Result.Failure(new Error(
                ErrorCodes.RouteNotFound,
                "The route was not found.",
                ErrorType.NotFound));
        }

        var now = timeProvider.GetUtcNow();
        var parse = provider.ParseWebhook(rawBody, headers, now);
        switch (parse.Status)
        {
            case FundingWebhookParseStatus.MissingOrStaleTimestamp:
                return Result.Failure(new Error(
                    ErrorCodes.WebhookStaleTimestamp,
                    "The webhook timestamp is missing or stale.",
                    ErrorType.Validation));
            case FundingWebhookParseStatus.InvalidSignature:
                await RecordInvalidSignatureAsync(providerKey, rawBody, now, cancellationToken);
                return Result.Failure(new Error(
                    ErrorCodes.WebhookInvalidSignature,
                    "The webhook signature is not valid.",
                    ErrorType.Unauthenticated));
            case FundingWebhookParseStatus.Malformed:
                return Result.Failure(new Error(
                    ErrorCodes.ValidationFailed,
                    "The webhook payload is not valid.",
                    ErrorType.Validation));
        }

        var webhookEvent = parse.Event!;
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();

        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            var dedupe = new FundingWebhookEvent
            {
                Id = Guid.CreateVersion7(),
                ProviderKey = providerKey,
                EventId = webhookEvent.EventId,
                ReceivedAt = now,
                Outcome = "received",
                BodyHash = bodyHash,
            };
            db.WebhookEvents.Add(dedupe);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
            {
                // A repeated event id is acknowledged without changing state.
                // The failed insert aborted the transaction, so roll back.
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Success();
            }

            if (webhookEvent.Kind == FundingWebhookKind.Deposit && webhookEvent.Deposit is not null)
            {
                await deposits.ApplyReportAsync(
                    providerKey, webhookEvent.Deposit, correlationId, cancellationToken);
                dedupe.Outcome = "processed";
            }
            else if (webhookEvent.Kind == FundingWebhookKind.Payout
                && webhookEvent.PayoutKey is not null
                && webhookEvent.PayoutState is { } payoutState)
            {
                // Payout events apply through the same routine as polling;
                // they join this transaction so dedupe and effect commit
                // together.
                await payoutProgress.ApplyReportAsync(
                    webhookEvent.PayoutKey, payoutState, webhookEvent.PayoutDetailCode,
                    cancellationToken);
                dedupe.Outcome = "payout_processed";
            }
            else
            {
                dedupe.Outcome = "payout_received";
            }

            await db.SaveChangesAsync(cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return Result.Success();
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task RecordInvalidSignatureAsync(
        string providerKey,
        string rawBody,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        db.WebhookEvents.Add(new FundingWebhookEvent
        {
            Id = Guid.CreateVersion7(),
            ProviderKey = providerKey,
            EventId = $"invalid:{bodyHash}",
            ReceivedAt = now,
            Outcome = "invalid_signature",
            BodyHash = bodyHash,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same invalid body was already recorded; the 401 stands.
        }
    }

    private async Task EnlistAsync(CancellationToken cancellationToken)
    {
        if (scope.Transaction is not null)
        {
            await db.Database.UseTransactionAsync(scope.Transaction, cancellationToken);
        }
    }

    private async Task CommitIfOwnerAsync(bool ownsScope, CancellationToken cancellationToken)
    {
        if (ownsScope)
        {
            await scope.CommitAsync(cancellationToken);
        }
    }

    private async Task RollbackAsync(bool ownsScope, CancellationToken cancellationToken)
    {
        if (ownsScope && scope.IsActive)
        {
            await scope.RollbackAsync(cancellationToken);
        }
    }
}

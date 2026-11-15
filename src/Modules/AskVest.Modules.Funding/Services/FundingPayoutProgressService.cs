using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Persistence;
using AskVest.Modules.Funding.Persistence.Entities;
using AskVest.Modules.Funding.Providers;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// Applies provider payout answers to cash withdrawals, from polling or
/// signed payout webhook events. Uncertainty never releases funds: pending,
/// unavailable and not-found answers only refresh the row, and a submitted
/// withdrawal the provider no longer knows becomes needs_reconciliation
/// after the submitted timeout.
/// </summary>
internal sealed class FundingPayoutProgressService(
    FundingDbContext db,
    FundingProviderRegistry providers,
    ILedger ledger,
    IOptions<FundingOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    FundingEventWriter events,
    ILogger<FundingPayoutProgressService> logger)
{
    /// <summary>Polls the provider for one submitted or reconciling withdrawal.</summary>
    public async Task ProcessAsync(Guid withdrawalId, CancellationToken cancellationToken)
    {
        var withdrawal = await db.Withdrawals.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == withdrawalId, cancellationToken);
        if (withdrawal is null
            || withdrawal.State is not (WithdrawalState.Submitted or WithdrawalState.NeedsReconciliation))
        {
            return;
        }

        var provider = ResolveProvider(withdrawal.Currency);
        if (provider is null)
        {
            return;
        }

        FundingPayoutStatus status;
        try
        {
            using var timeoutSource = new CancellationTokenSource(
                TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds), timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            status = await provider.GetPayoutStatusAsync(withdrawal.Id.ToString("N"), linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (FundingProviderException exception)
        {
            logger.LogWarning(
                "Payout status poll failed for withdrawal {WithdrawalId} with {ReasonCode}.",
                withdrawalId, exception.ReasonCode ?? exception.Kind.ToString());
            return;
        }

        await ApplyAsync(withdrawal.Id, status.State, status.DetailCode, cancellationToken);
    }

    /// <summary>
    /// Applies a provider-reported payout state, shared by the polling path
    /// and payout webhook events. Unknown withdrawal keys are ignored.
    /// </summary>
    public async Task ApplyReportAsync(
        string withdrawalKey,
        FundingPayoutReportState state,
        string? detailCode,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(withdrawalKey, "N", out var withdrawalId))
        {
            return;
        }

        await ApplyAsync(withdrawalId, state, detailCode, cancellationToken);
    }

    private async Task ApplyAsync(
        Guid withdrawalId,
        FundingPayoutReportState state,
        string? detailCode,
        CancellationToken cancellationToken)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM funding.withdrawals WHERE id = {withdrawalId} FOR UPDATE",
                cancellationToken);
            var withdrawal = await db.Withdrawals
                .FirstOrDefaultAsync(row => row.Id == withdrawalId, cancellationToken);
            if (withdrawal is null
                || withdrawal.State is not (WithdrawalState.Submitted or WithdrawalState.NeedsReconciliation))
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return;
            }

            var now = timeProvider.GetUtcNow();
            var from = withdrawal.State;
            switch (state)
            {
                case FundingPayoutReportState.Completed:
                    await SettleAsync(withdrawal, now, cancellationToken);
                    break;
                case FundingPayoutReportState.Failed:
                    await FailAsync(withdrawal, detailCode ?? "payout_failed", now, cancellationToken);
                    break;
                case FundingPayoutReportState.NotFound
                    when withdrawal.State == WithdrawalState.Submitted
                        && withdrawal.SubmittedAt is { } submittedAt
                        && now - submittedAt > TimeSpan.FromSeconds(options.Value.SubmittedTimeoutSeconds):
                    withdrawal.State = WithdrawalState.NeedsReconciliation;
                    withdrawal.DetailCode = "status_not_found";
                    withdrawal.UpdatedAt = now;
                    break;
                default:
                    withdrawal.UpdatedAt = now;
                    break;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (withdrawal.State != from)
            {
                events.Write(
                    withdrawal.UserId, FundingEventType.WithdrawalStateChanged, "withdrawal", withdrawal.Id,
                    FundingText.ToDb(from), FundingText.ToDb(withdrawal.State), null, withdrawal.DetailCode);
                await outbox.EnqueueAsync(
                    FundingWithdrawalService.StateChangedMessageType,
                    new { withdrawalId = withdrawal.Id },
                    $"funding:state:{withdrawal.Id:N}:{FundingText.ToDb(withdrawal.State)}",
                    cancellationToken);
            }

            await CommitIfOwnerAsync(ownsScope, cancellationToken);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task SettleAsync(
        FundingWithdrawal withdrawal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var settle = FundingWithdrawalJournals.Settle(
            withdrawal.Id, withdrawal.UserId, withdrawal.Currency,
            withdrawal.ReceiveAmount, withdrawal.FeeAmount, withdrawal.TotalDebited);
        var posted = await ledger.PostJournalAsync(settle, cancellationToken);
        if (posted.IsFailure)
        {
            withdrawal.State = WithdrawalState.NeedsReconciliation;
            withdrawal.DetailCode = "settle_failed";
            withdrawal.UpdatedAt = now;
            return;
        }

        withdrawal.State = WithdrawalState.Completed;
        withdrawal.SettleJournalId = posted.Value.Id;
        withdrawal.CompletedAt = now;
        withdrawal.UpdatedAt = now;
    }

    private async Task FailAsync(
        FundingWithdrawal withdrawal,
        string detailCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        withdrawal.State = WithdrawalState.Failed;
        withdrawal.DetailCode = detailCode;
        withdrawal.FailedAt = now;
        withdrawal.UpdatedAt = now;
        var released = await ledger.PostJournalAsync(
            FundingWithdrawalJournals.Release(
                withdrawal.Id, withdrawal.UserId, withdrawal.Currency, withdrawal.TotalDebited),
            cancellationToken);
        if (released.IsFailure)
        {
            withdrawal.State = WithdrawalState.NeedsReconciliation;
            withdrawal.DetailCode = "release_failed";
            return;
        }

        withdrawal.ReleaseJournalId = released.Value.Id;
    }

    private IFundingProvider? ResolveProvider(string currency)
    {
        var option = options.Value.Currencies.FirstOrDefault(
            candidate => string.Equals(candidate.Code, currency, StringComparison.Ordinal));
        return option is not null && providers.TryGet(option.Provider, out var provider)
            ? provider
            : null;
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

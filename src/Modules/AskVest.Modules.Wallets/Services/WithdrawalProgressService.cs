using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Wallets.Custody;
using AskVest.Modules.Wallets.Domain;
using AskVest.Modules.Wallets.Persistence;
using AskVest.Modules.Wallets.Persistence.Entities;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AskVest.Modules.Wallets.Configuration;

namespace AskVest.Modules.Wallets.Services;

/// <summary>
/// Polls submitted and needs-reconciliation withdrawals against the custody
/// provider and applies definitive answers. Uncertainty never releases
/// funds: pending, unavailable and not-found answers only refresh the row.
/// </summary>
internal sealed class WithdrawalProgressService(
    WalletsDbContext db,
    ICatalog catalog,
    CustodyRegistry custody,
    ILedger ledger,
    IOptions<WalletsOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    WalletEventWriter events,
    ILogger<WithdrawalProgressService> logger)
{
    public async Task ProcessAsync(Guid withdrawalId, CancellationToken cancellationToken)
    {
        var withdrawal = await db.Withdrawals.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == withdrawalId, cancellationToken);
        if (withdrawal is null
            || withdrawal.State is not (WithdrawalState.Submitted or WithdrawalState.NeedsReconciliation))
        {
            return;
        }

        var token = await catalog.FindTokenAsync(withdrawal.TokenId, cancellationToken);
        if (token is null || !custody.TryResolve(withdrawal.DestinationChainId, out var provider))
        {
            return;
        }

        CustodyWithdrawalStatus status;
        var timeout = TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds);
        try
        {
            using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            status = await provider.GetWithdrawalStatusAsync(
                withdrawal.Id.ToString("N"), linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            status = new CustodyWithdrawalStatus(CustodyWithdrawalStatusKind.Unavailable, null, "timeout");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Custody status poll failed for withdrawal {WithdrawalId}.", withdrawalId);
            status = new CustodyWithdrawalStatus(CustodyWithdrawalStatusKind.Unavailable, null, "error");
        }

        await ApplyStatusAsync(withdrawal.Id, status, cancellationToken);
    }

    private async Task ApplyStatusAsync(
        Guid withdrawalId,
        CustodyWithdrawalStatus status,
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
                $"SELECT 1 FROM wallets.withdrawals WHERE id = {withdrawalId} FOR UPDATE",
                cancellationToken);
            var withdrawal = await db.Withdrawals
                .FirstOrDefaultAsync(row => row.Id == withdrawalId, cancellationToken);
            if (withdrawal is null
                || !WithdrawalTransitions.IsNonFinal(withdrawal.State)
                || withdrawal.State == WithdrawalState.Requested
                || withdrawal.State == WithdrawalState.Submitting)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return;
            }

            var now = timeProvider.GetUtcNow();
            withdrawal.LastStatusCheckAt = now;
            var from = withdrawal.State;
            switch (status.Kind)
            {
                case CustodyWithdrawalStatusKind.Completed:
                    await SettleAsync(withdrawal, status, now, cancellationToken);
                    break;
                case CustodyWithdrawalStatusKind.Failed:
                    await FailAsync(withdrawal, status.ReasonCode ?? "custody_failed", now, cancellationToken);
                    break;
                case CustodyWithdrawalStatusKind.NotFound
                    when withdrawal.State == WithdrawalState.Submitted
                        && withdrawal.SubmittedAt is { } submittedAt
                        && now - submittedAt > TimeSpan.FromSeconds(options.Value.SubmittedTimeoutSeconds):
                    // A submitted withdrawal the provider no longer knows is
                    // ambiguous, not failed: keep the hold and reconcile.
                    withdrawal.State = WithdrawalState.NeedsReconciliation;
                    withdrawal.DetailCode = "status_not_found";
                    withdrawal.UpdatedAt = now;
                    break;
                case CustodyWithdrawalStatusKind.NotFound:
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
                    withdrawal.UserId, WalletEventType.WithdrawalStateChanged, "withdrawal", withdrawal.Id,
                    WalletsText.ToDb(from), WalletsText.ToDb(withdrawal.State), null, withdrawal.DetailCode);
                await outbox.EnqueueAsync(
                    "WalletsWithdrawalStateChanged",
                    new { withdrawalId = withdrawal.Id },
                    $"wallets:state:{withdrawal.Id:N}:{WalletsText.ToDb(withdrawal.State)}",
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
        Withdrawal withdrawal,
        CustodyWithdrawalStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var token = await catalog.FindTokenAsync(withdrawal.TokenId, cancellationToken);
        if (token is null)
        {
            withdrawal.State = WithdrawalState.NeedsReconciliation;
            withdrawal.DetailCode = "token_unknown_at_settle";
            withdrawal.UpdatedAt = now;
            return;
        }

        var settle = WithdrawalJournals.Settle(
            withdrawal.Id, withdrawal.UserId, token.LedgerAssetCode,
            withdrawal.GrossQuantity, withdrawal.Fee);
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
        withdrawal.SettlementReference = status.SettlementReference;
        withdrawal.CompletedAt = now;
        withdrawal.UpdatedAt = now;
    }

    private async Task FailAsync(
        Withdrawal withdrawal,
        string detailCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        withdrawal.State = WithdrawalState.Failed;
        withdrawal.DetailCode = detailCode;
        withdrawal.FailedAt = now;
        withdrawal.UpdatedAt = now;
        if (withdrawal.HoldJournalId is { } holdJournalId)
        {
            var released = await ledger.ReverseJournalAsync(
                WithdrawalJournals.ReleaseKey(withdrawal.Id), holdJournalId, cancellationToken);
            if (released.IsSuccess)
            {
                withdrawal.ReleaseJournalId = released.Value.Id;
            }
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

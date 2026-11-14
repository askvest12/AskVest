using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Wallets.Configuration;
using AskVest.Modules.Wallets.Custody;
using AskVest.Modules.Wallets.Domain;
using AskVest.Modules.Wallets.Persistence;
using AskVest.Modules.Wallets.Persistence.Entities;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets.Services;

/// <summary>
/// Delivers one requested withdrawal to the custody provider. The state
/// claim and the acknowledgement each run in their own transaction; the
/// provider call itself never holds a database transaction open. Our
/// withdrawal id is the provider idempotency reference.
/// </summary>
internal sealed class WithdrawalSubmitService(
    WalletsDbContext db,
    ICatalog catalog,
    ILedger ledger,
    CustodyRegistry custody,
    IOptions<WalletsOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    WalletEventWriter events,
    IOutbox outbox,
    ILogger<WithdrawalSubmitService> logger)
{
    public async Task SubmitAsync(Guid withdrawalId, CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(withdrawalId, cancellationToken);
        if (claimed is null)
        {
            return;
        }

        var withdrawal = claimed;
        var token = await catalog.FindTokenAsync(withdrawal.TokenId, cancellationToken);
        if (token is null || !custody.TryResolve(withdrawal.DestinationChainId, out var provider))
        {
            await ApplyUnknownAsync(withdrawal.Id, "custody_unresolvable", cancellationToken);
            return;
        }

        var request = new CustodyWithdrawRequest(
            withdrawal.Id.ToString("N"),
            withdrawal.DestinationChainId,
            token.CanonicalAddress,
            token.Decimals,
            withdrawal.DestinationAddress,
            withdrawal.NetQuantity);

        CustodySubmitResult result;
        var timeout = TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds);
        try
        {
            using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            result = await provider.SubmitWithdrawalAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new CustodySubmitResult(CustodySubmitKind.Unknown, null, "timeout");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Custody submission failed for withdrawal {WithdrawalId}.", withdrawalId);
            result = new CustodySubmitResult(CustodySubmitKind.Unknown, null, "error");
        }

        await ApplyResultAsync(withdrawal.Id, result, cancellationToken);
    }

    private async Task<Withdrawal?> ClaimAsync(Guid withdrawalId, CancellationToken cancellationToken)
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
            if (withdrawal is null || withdrawal.State != WithdrawalState.Requested)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return null;
            }

            withdrawal.State = WithdrawalState.Submitting;
            withdrawal.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return withdrawal;
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task ApplyResultAsync(
        Guid withdrawalId,
        CustodySubmitResult result,
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
            if (withdrawal is null || withdrawal.State != WithdrawalState.Submitting)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return;
            }

            var now = timeProvider.GetUtcNow();
            var from = withdrawal.State;
            switch (result.Kind)
            {
                case CustodySubmitKind.Acknowledged:
                    withdrawal.State = WithdrawalState.Submitted;
                    withdrawal.ProviderReference = result.ProviderReference;
                    withdrawal.SubmittedAt = now;
                    withdrawal.UpdatedAt = now;
                    break;
                case CustodySubmitKind.Rejected:
                    await FailAsync(withdrawal, result.ReasonCode ?? "rejected", now, cancellationToken);
                    break;
                default:
                    withdrawal.State = WithdrawalState.NeedsReconciliation;
                    withdrawal.DetailCode = result.ReasonCode ?? "submit_unknown";
                    withdrawal.UpdatedAt = now;
                    break;
            }

            await db.SaveChangesAsync(cancellationToken);
            events.Write(
                withdrawal.UserId, WalletEventType.WithdrawalStateChanged, "withdrawal", withdrawal.Id,
                WalletsText.ToDb(from), WalletsText.ToDb(withdrawal.State), null, withdrawal.DetailCode);
            await outbox.EnqueueAsync(
                "WalletsWithdrawalStateChanged",
                new { withdrawalId = withdrawal.Id },
                $"wallets:state:{withdrawal.Id:N}:{WalletsText.ToDb(withdrawal.State)}",
                cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
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

    private async Task ApplyUnknownAsync(
        Guid withdrawalId,
        string detailCode,
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
            if (withdrawal is not null && withdrawal.State == WithdrawalState.Submitting)
            {
                withdrawal.State = WithdrawalState.NeedsReconciliation;
                withdrawal.DetailCode = detailCode;
                withdrawal.UpdatedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
            }

            await CommitIfOwnerAsync(ownsScope, cancellationToken);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
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

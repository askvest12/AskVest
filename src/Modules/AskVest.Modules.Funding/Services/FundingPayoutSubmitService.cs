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
/// Delivers one requested cash withdrawal to the funding provider. The
/// state claim and the acknowledgement each run in their own transaction;
/// the provider call itself never holds a database transaction open. Our
/// withdrawal id is the provider idempotency key.
/// </summary>
internal sealed class FundingPayoutSubmitService(
    FundingDbContext db,
    FundingProviderRegistry providers,
    ILedger ledger,
    IOptions<FundingOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    FundingEventWriter events,
    ILogger<FundingPayoutSubmitService> logger)
{
    public async Task SubmitAsync(Guid withdrawalId, CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(withdrawalId, cancellationToken);
        if (claimed is null)
        {
            return;
        }

        var withdrawal = claimed;
        var provider = ResolveProvider(withdrawal.Currency);
        var beneficiary = await db.Beneficiaries.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Id == withdrawal.BeneficiaryId, cancellationToken);
        if (provider is null || beneficiary is null)
        {
            await ApplyUnknownAsync(withdrawal.Id, "payout_unresolvable", cancellationToken);
            return;
        }

        var key = withdrawal.Id.ToString("N");
        try
        {
            using var timeoutSource = new CancellationTokenSource(
                TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds), timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            await provider.RequestPayoutAsync(
                key, beneficiary.ProviderReference, withdrawal.Currency,
                withdrawal.ReceiveAmount, linked.Token);
            await ApplySubmittedAsync(withdrawal.Id, key, cancellationToken);
        }
        catch (FundingProviderException exception)
        {
            if (exception.Kind == FundingProviderFailureKind.Rejected)
            {
                await ApplyRejectedAsync(withdrawal.Id, exception.ReasonCode ?? "payout_rejected",
                    cancellationToken);
            }
            else
            {
                await ApplyUnknownAsync(
                    withdrawal.Id, exception.ReasonCode ?? "submit_unknown", cancellationToken);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ApplyUnknownAsync(withdrawal.Id, "submit_timeout", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Payout submission failed for withdrawal {WithdrawalId}.", withdrawalId);
            await ApplyUnknownAsync(withdrawal.Id, "submit_error", cancellationToken);
        }
    }

    private IFundingProvider? ResolveProvider(string currency)
    {
        var option = options.Value.Currencies.FirstOrDefault(
            candidate => string.Equals(candidate.Code, currency, StringComparison.Ordinal));
        return option is not null && providers.TryGet(option.Provider, out var provider)
            ? provider
            : null;
    }

    private async Task<FundingWithdrawal?> ClaimAsync(Guid withdrawalId, CancellationToken cancellationToken)
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

    private Task ApplySubmittedAsync(
        Guid withdrawalId,
        string key,
        CancellationToken cancellationToken)
        => ApplyAsync(withdrawalId, WithdrawalState.Submitted, null, (row, _) =>
        {
            row.ProviderReference = key;
            row.SubmittedAt = timeProvider.GetUtcNow();
            return Task.CompletedTask;
        }, cancellationToken);

    private async Task ApplyRejectedAsync(
        Guid withdrawalId,
        string detailCode,
        CancellationToken cancellationToken)
        => await ApplyAsync(withdrawalId, WithdrawalState.Failed, detailCode, async (row, ct) =>
        {
            row.FailedAt = timeProvider.GetUtcNow();
            await ReleaseHoldAsync(row, ct);
        }, cancellationToken);

    private async Task ApplyUnknownAsync(
        Guid withdrawalId,
        string detailCode,
        CancellationToken cancellationToken)
        => await ApplyAsync(withdrawalId, WithdrawalState.NeedsReconciliation, detailCode,
            (_, _) => Task.CompletedTask, cancellationToken);

    private async Task ApplyAsync(
        Guid withdrawalId,
        WithdrawalState target,
        string? detailCode,
        Func<FundingWithdrawal, CancellationToken, Task> extra,
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
            if (withdrawal is null || withdrawal.State != WithdrawalState.Submitting)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return;
            }

            var from = withdrawal.State;
            var now = timeProvider.GetUtcNow();
            withdrawal.State = target;
            withdrawal.DetailCode = detailCode;
            withdrawal.UpdatedAt = now;
            await extra(withdrawal, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            var finalState = withdrawal.State;
            events.Write(
                withdrawal.UserId, FundingEventType.WithdrawalStateChanged, "withdrawal", withdrawal.Id,
                FundingText.ToDb(from), FundingText.ToDb(finalState), null, withdrawal.DetailCode);
            await outbox.EnqueueAsync(
                FundingWithdrawalService.StateChangedMessageType,
                new { withdrawalId = withdrawal.Id },
                $"funding:state:{withdrawal.Id:N}:{FundingText.ToDb(finalState)}",
                cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Releases the hold through an explicit release journal. A release
    /// failure parks the row in reconciliation rather than losing the hold.
    /// </summary>
    internal async Task<bool> ReleaseHoldAsync(
        FundingWithdrawal withdrawal,
        CancellationToken cancellationToken)
    {
        var released = await ledger.PostJournalAsync(
            FundingWithdrawalJournals.Release(
                withdrawal.Id, withdrawal.UserId, withdrawal.Currency, withdrawal.TotalDebited),
            cancellationToken);
        if (released.IsFailure)
        {
            withdrawal.State = WithdrawalState.NeedsReconciliation;
            withdrawal.DetailCode = "release_failed";
            return false;
        }

        withdrawal.ReleaseJournalId = released.Value.Id;
        return true;
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

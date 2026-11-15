using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Contracts;
using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Persistence;
using AskVest.Modules.Funding.Persistence.Entities;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// Operator resolution for review, quarantine, restriction and pause cases.
/// Reachable only through the module contract; no HTTP route exposes it.
/// Every action writes a funding event.
/// </summary>
internal sealed class FundingOperatorService(
    FundingDbContext db,
    ILedger ledger,
    ITransactionScope scope,
    IOutbox outbox,
    FundingEventWriter events,
    IOptions<FundingOptions> fundingOptions,
    TimeProvider timeProvider) : IFundingOperator
{
    public async Task<Result> ApproveWithdrawalAsync(
        Guid withdrawalId,
        CancellationToken cancellationToken = default)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            var row = await db.Withdrawals
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == withdrawalId
                        && candidate.State == WithdrawalState.UnderReview,
                    cancellationToken);
            if (row is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure(new Error(
                    ErrorCodes.ResourceConflict,
                    "The withdrawal is not under review.",
                    ErrorType.Conflict));
            }

            var from = FundingText.ToDb(row.State);
            var now = timeProvider.GetUtcNow();
            row.State = WithdrawalState.Requested;
            row.ReviewDecision = "approved";
            row.ReviewedAt = now;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            events.Write(
                row.UserId, FundingEventType.OperatorAction, "withdrawal", row.Id,
                from, FundingText.ToDb(WithdrawalState.Requested), null, "review_approved");
            await outbox.EnqueueAsync(
                FundingWithdrawalService.SubmitMessageType,
                new { withdrawalId = row.Id },
                $"funding:submit:{row.Id:N}",
                cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return Result.Success();
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    public async Task<Result> RejectWithdrawalAsync(
        Guid withdrawalId,
        CancellationToken cancellationToken = default)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            var row = await db.Withdrawals
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == withdrawalId
                        && candidate.State == WithdrawalState.UnderReview,
                    cancellationToken);
            if (row is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure(new Error(
                    ErrorCodes.ResourceConflict,
                    "The withdrawal is not under review.",
                    ErrorType.Conflict));
            }

            var released = await ledger.PostJournalAsync(
                FundingWithdrawalJournals.Release(
                    row.Id, row.UserId, row.Currency, row.TotalDebited),
                cancellationToken);
            if (released.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure(released.Error!);
            }

            var from = FundingText.ToDb(row.State);
            var now = timeProvider.GetUtcNow();
            row.State = WithdrawalState.Failed;
            row.ReviewDecision = "rejected";
            row.ReviewedAt = now;
            row.ReleaseJournalId = released.Value.Id;
            row.FailedAt = now;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            events.Write(
                row.UserId, FundingEventType.OperatorAction, "withdrawal", row.Id,
                from, FundingText.ToDb(WithdrawalState.Failed), null, "review_rejected");
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return Result.Success();
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    public async Task<Result> ResolveDepositAsync(
        Guid depositId,
        bool credit,
        CancellationToken cancellationToken = default)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            var row = await db.Deposits
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == depositId
                        && candidate.State == DepositState.Quarantined,
                    cancellationToken);
            if (row is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure(new Error(
                    ErrorCodes.ResourceConflict,
                    "The deposit is not quarantined.",
                    ErrorType.Conflict));
            }

            var now = timeProvider.GetUtcNow();
            var from = FundingText.ToDb(row.State);
            if (credit)
            {
                var registered = await ledger.RegisterAssetAsync(
                    row.Currency, AssetKind.Fiat, ScaleFor(row.Currency), cancellationToken);
                if (registered.IsFailure)
                {
                    await RollbackAsync(ownsScope, cancellationToken);
                    return Result.Failure(registered.Error!);
                }

                var journal = await ledger.PostJournalAsync(new PostJournalRequest(
                    $"funding:deposit:{row.Id:N}",
                    Book.Real,
                    LedgerReasonCodes.Deposit,
                    "funding_deposit",
                    row.Id,
                    [
                        new LedgerLine(LedgerAccounts.SystemClearing(Book.Real), row.Currency, -row.Amount),
                        new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, row.UserId), row.Currency, row.Amount),
                    ]), cancellationToken);
                if (journal.IsFailure)
                {
                    await RollbackAsync(ownsScope, cancellationToken);
                    return Result.Failure(journal.Error!);
                }

                row.CreditJournalId = journal.Value.Id;
                row.State = DepositState.Credited;
                row.CreditedAt = now;
            }
            else
            {
                row.State = DepositState.Failed;
                row.DetailCode = "returned";
            }

            row.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            events.Write(
                row.UserId, FundingEventType.OperatorAction, "deposit", row.Id,
                from, FundingText.ToDb(row.State), null,
                credit ? "quarantine_credit" : "quarantine_return");
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return Result.Success();
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    public async Task<Result> ClearRestrictionAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            var rows = await db.Restrictions
                .Where(row => row.UserId == userId && row.ClearedAt == null)
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return Result.Success();
            }

            var now = timeProvider.GetUtcNow();
            foreach (var row in rows)
            {
                row.ClearedAt = now;
                events.Write(
                    userId, FundingEventType.RestrictionCleared, "funding_restriction", row.Id,
                    null, null, null);
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

    public async Task<Result> ClearPauseAsync(
        string currency,
        CancellationToken cancellationToken = default)
    {
        var code = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            var pauses = await db.Pauses
                .Where(row => row.Currency == code)
                .ToListAsync(cancellationToken);
            foreach (var pause in pauses)
            {
                events.Write(
                    Guid.Empty, FundingEventType.PauseChanged, "funding_pause", pause.Id,
                    "paused", "cleared", null, pause.ReasonCode);
            }

            await db.Pauses.Where(row => row.Currency == code)
                .ExecuteDeleteAsync(cancellationToken);
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

    public async Task<Result<IReadOnlyList<FundingCashBalance>>> GetCashBalancesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var balances = await ledger.GetBalancesAsync(userId, Book.Real, cancellationToken);
        if (balances.IsFailure)
        {
            return Result.Failure<IReadOnlyList<FundingCashBalance>>(balances.Error!);
        }

        var rows = balances.Value
            .GroupBy(balance => balance.AssetCode, StringComparer.Ordinal)
            .Select(group => new FundingCashBalance(
                group.Key,
                group.Sum(balance => balance.Available),
                group.Sum(balance => balance.Held)))
            .OrderBy(row => row.Currency, StringComparer.Ordinal)
            .ToArray();
        return Result.Success<IReadOnlyList<FundingCashBalance>>(rows);
    }

    private int ScaleFor(string currency)
        => fundingOptions.Value.Currencies
            .FirstOrDefault(candidate => string.Equals(
                candidate.Code, currency, StringComparison.Ordinal))
            ?.Scale ?? 2;

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

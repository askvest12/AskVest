using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Practice.Configuration;
using AskVest.Modules.Practice.Domain;
using AskVest.Modules.Practice.Persistence;
using AskVest.Modules.Practice.Persistence.Entities;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Practice.Services;

internal sealed class PracticeAccountService(
    PracticeDbContext db,
    ILedger ledger,
    IOptions<PracticeOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    PracticeEventWriter events)
{
    private const string CashAsset = "USD";

    public async Task<Result<PracticeAccount>> StartAsync(
        Guid userId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await PracticeScope.EnlistAsync(db, scope, cancellationToken);
            var existing = await db.PracticeAccounts.AsNoTracking()
                .FirstOrDefaultAsync(account => account.UserId == userId, cancellationToken);
            if (existing is not null)
            {
                if (ownsScope)
                {
                    await scope.CommitAsync(cancellationToken);
                }

                return Result.Success(existing);
            }

            var now = timeProvider.GetUtcNow();
            var credits = options.Value.StartingCredits;
            var account = new PracticeAccount
            {
                UserId = userId,
                StartedAt = now,
                ResetCount = 0,
                StartingCreditsUsed = credits,
                UpdatedAt = now,
            };
            db.PracticeAccounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);

            var granted = await PostGrantAsync(userId, 0, credits, cancellationToken);
            if (granted.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeAccount>(granted.Error!);
            }

            events.Write(userId, PracticeEventType.AccountStarted, userId, correlationId);
            await db.SaveChangesAsync(cancellationToken);

            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            return Result.Success(account);
        }
        catch (DbUpdateException exception) when (ownsScope
            && exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            })
        {
            // A parallel start won the primary key; the grant was never posted.
            await scope.RollbackAsync(cancellationToken);
            var raced = await db.PracticeAccounts.AsNoTracking()
                .FirstAsync(account => account.UserId == userId, cancellationToken);
            return Result.Success(raced);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    public async Task<Result<PracticeAccountStatus>> GetStatusAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var account = await db.PracticeAccounts.AsNoTracking()
            .FirstOrDefaultAsync(row => row.UserId == userId, cancellationToken);
        if (account is null)
        {
            return Result.Success(new PracticeAccountStatus(false, 0m, 0, 0, null));
        }

        var balances = await ledger.GetBalancesAsync(userId, Book.Practice, cancellationToken);
        var cash = balances.IsSuccess
            ? balances.Value.FirstOrDefault(balance => balance.AssetCode == CashAsset)?.Available ?? 0m
            : 0m;

        var now = timeProvider.GetUtcNow();
        var resetsInWindow = await CountResetsAsync(userId, now, cancellationToken);
        var decision = Decide(account, now, resetsInWindow);
        return Result.Success(new PracticeAccountStatus(
            true,
            cash,
            account.ResetCount,
            decision.ResetsLeft,
            decision.NextAllowedAt));
    }

    public async Task<Result<PracticeAccountStatus>> ResetAsync(
        Guid userId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await PracticeScope.EnlistAsync(db, scope, cancellationToken);

            // Lock the account row first so concurrent resets serialize; the
            // policy is then evaluated against the locked, latest values.
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM practice.practice_accounts WHERE user_id = {userId} FOR UPDATE",
                cancellationToken);
            var account = await db.PracticeAccounts
                .FirstOrDefaultAsync(row => row.UserId == userId, cancellationToken);
            if (account is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeAccountStatus>(PracticeResults.AccountNotStarted());
            }

            var now = timeProvider.GetUtcNow();
            var resetsInWindow = await CountResetsAsync(userId, now, cancellationToken);
            var decision = Decide(account, now, resetsInWindow);
            if (!decision.Allowed)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeAccountStatus>(new Error(
                    ErrorCodes.PracticeResetNotAllowed,
                    $"The practice account cannot be reset yet: {decision.Reason}.",
                    ErrorType.BusinessRule));
            }

            var cancelled = await CancelOpenOrdersAsync(userId, now, correlationId, cancellationToken);
            if (cancelled.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeAccountStatus>(cancelled.Error!);
            }

            var swept = await SweepBalancesAsync(userId, account.ResetCount + 1, cancellationToken);
            if (swept.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeAccountStatus>(swept.Error!);
            }

            account.ResetCount += 1;
            account.LastResetAt = now;
            account.StartingCreditsUsed += options.Value.StartingCredits;
            account.UpdatedAt = now;

            var granted = await PostGrantAsync(
                userId, account.ResetCount, options.Value.StartingCredits, cancellationToken);
            if (granted.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<PracticeAccountStatus>(granted.Error!);
            }

            events.Write(userId, PracticeEventType.AccountReset, userId, correlationId);
            await db.SaveChangesAsync(cancellationToken);

            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            // The sweep zeroes every balance and the grant restores exactly the
            // starting credits, so the post-reset status is known in memory.
            var after = PracticeResetPolicy.Evaluate(
                now, now, resetsInWindow + 1, options.Value);
            return Result.Success(new PracticeAccountStatus(
                true,
                options.Value.StartingCredits,
                account.ResetCount,
                after.ResetsLeft,
                after.NextAllowedAt));
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    internal async Task<Result> PostGrantAsync(
        Guid userId,
        int resetNumber,
        decimal credits,
        CancellationToken cancellationToken)
    {
        var posted = await ledger.PostJournalAsync(
            new PostJournalRequest(
                $"practice:grant:{userId:N}:{resetNumber}",
                Book.Practice,
                LedgerReasonCodes.PracticeCreditGrant,
                "practice_account",
                userId,
                [
                    new LedgerLine(LedgerAccounts.UserAvailable(Book.Practice, userId), CashAsset, credits),
                    new LedgerLine(LedgerAccounts.PracticeIssuance(Book.Practice), CashAsset, -credits),
                ]),
            cancellationToken);
        return posted.IsSuccess ? Result.Success() : Result.Failure(posted.Error!);
    }

    private async Task<int> CountResetsAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
        => await db.PracticeEvents.CountAsync(
            practiceEvent => practiceEvent.UserId == userId
                && practiceEvent.EventType == PracticeEventType.AccountReset
                && practiceEvent.OccurredAt > now - PracticeResetPolicy.ResetWindow,
            cancellationToken);

    private PracticeResetPolicy.PracticeResetDecision Decide(
        PracticeAccount account,
        DateTimeOffset now,
        int resetsInWindow)
        => PracticeResetPolicy.Evaluate(
            account.LastResetAt ?? account.StartedAt,
            now,
            resetsInWindow,
            options.Value);

    private async Task<Result> CancelOpenOrdersAsync(
        Guid userId,
        DateTimeOffset now,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var openOrders = await db.PracticeOrders
            .Where(order => order.UserId == userId && order.State == PracticeOrderState.Open)
            .ToListAsync(cancellationToken);
        foreach (var order in openOrders)
        {
            if (order.HoldJournalId is { } holdJournalId)
            {
                var released = await ledger.ReverseJournalAsync(
                    $"practice:order:{order.Id:N}:release", holdJournalId, cancellationToken);
                if (released.IsFailure)
                {
                    return Result.Failure(released.Error!);
                }
            }

            order.State = PracticeOrderState.Cancelled;
            order.CancelledAt = now;
            order.CancelReason = "reset";
            events.Write(userId, PracticeEventType.OrderCancelled, order.Id, correlationId);
        }

        return Result.Success();
    }

    private async Task<Result> SweepBalancesAsync(
        Guid userId,
        int resetNumber,
        CancellationToken cancellationToken)
    {
        var balances = await ledger.GetBalancesAsync(userId, Book.Practice, cancellationToken);
        if (balances.IsFailure)
        {
            return Result.Failure(balances.Error!);
        }

        var lines = new List<LedgerLine>();
        foreach (var balance in balances.Value)
        {
            if (balance.Available == 0m && balance.Held == 0m)
            {
                continue;
            }

            var isCash = balance.AssetCode == CashAsset;
            var payback = isCash ? LedgerAccounts.PracticeIssuance(Book.Practice) : LedgerAccounts.SystemClearing(Book.Practice);
            var userAvailable = LedgerAccounts.UserAvailable(Book.Practice, userId);
            var userHeld = LedgerAccounts.UserHeld(Book.Practice, userId);
            if (balance.Available != 0m)
            {
                lines.Add(new LedgerLine(userAvailable, balance.AssetCode, -balance.Available));
                lines.Add(new LedgerLine(payback, balance.AssetCode, balance.Available));
            }

            if (balance.Held != 0m)
            {
                lines.Add(new LedgerLine(userHeld, balance.AssetCode, -balance.Held));
                lines.Add(new LedgerLine(payback, balance.AssetCode, balance.Held));
            }
        }

        if (lines.Count == 0)
        {
            return Result.Success();
        }

        var swept = await ledger.PostJournalAsync(
            new PostJournalRequest(
                $"practice:reset:{userId:N}:{resetNumber}",
                Book.Practice,
                LedgerReasonCodes.PracticeReset,
                "practice_account",
                userId,
                lines),
            cancellationToken);
        return swept.IsSuccess ? Result.Success() : Result.Failure(swept.Error!);
    }

    private async Task RollbackAsync(bool ownsScope, CancellationToken cancellationToken)
    {
        if (ownsScope)
        {
            await scope.RollbackAsync(cancellationToken);
        }
    }
}

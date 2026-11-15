using System.Globalization;
using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Persistence;
using AskVest.Modules.Funding.Persistence.Entities;
using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// Cash withdrawal previews and placement. A preview stores the terms and
/// expiry; placement re-checks every rule under the per-user lock, consumes
/// the step-up grant, reserves the total through a hold journal and either
/// enqueues submission or parks the withdrawal under review.
/// </summary>
internal sealed class FundingWithdrawalService(
    FundingDbContext db,
    ILedger ledger,
    IUserDirectory users,
    IStepUpAuthorizer stepUp,
    IOptions<FundingOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    FundingEventWriter events,
    ILogger<FundingWithdrawalService> logger)
{
    public const string SubmitMessageType = "FundingWithdrawalSubmit";

    public const string StateChangedMessageType = "FundingWithdrawalStateChanged";

    public async Task<Result<FundingWithdrawal>> PreviewAsync(
        Guid userId,
        string currency,
        string? receiveAmountText,
        Guid beneficiaryId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var option = ResolveCurrency(currency);
        if (option is null)
        {
            return Result.Failure<FundingWithdrawal>(FundingResults.NotAvailable("method_unsupported"));
        }

        var availabilityError = await CheckAvailabilityAsync(userId, option.Code, cancellationToken);
        if (availabilityError is not null)
        {
            return Result.Failure<FundingWithdrawal>(availabilityError);
        }

        var beneficiary = await db.Beneficiaries.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.UserId == userId
                    && row.Id == beneficiaryId
                    && row.Currency == option.Code
                    && row.State != BeneficiaryState.Removed,
                cancellationToken);
        if (beneficiary is null)
        {
            return Result.Failure<FundingWithdrawal>(FundingResults.NotFound("beneficiary"));
        }

        var now = timeProvider.GetUtcNow();
        var amounts = await ComputeAmountsAsync(
            userId, option, receiveAmountText, now, cancellationToken);
        if (amounts.IsFailure)
        {
            return Result.Failure<FundingWithdrawal>(amounts.Error!);
        }

        var expiresAt = now + TimeSpan.FromSeconds(options.Value.WithdrawalPreviewLifetimeSeconds);
        var withdrawal = new FundingWithdrawal
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Currency = option.Code,
            BeneficiaryId = beneficiary.Id,
            ReceiveAmount = amounts.Value.Receive,
            FeeAmount = amounts.Value.Fee,
            TotalDebited = amounts.Value.Total,
            TermsHash = FundingMath.WithdrawalTermsHash(
                beneficiary.Id, option.Code, amounts.Value.Receive, amounts.Value.Fee,
                amounts.Value.Total, expiresAt),
            State = WithdrawalState.Previewed,
            ExpiresAt = expiresAt,
            IdempotencyKey = $"preview:{Guid.CreateVersion7():N}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Withdrawals.Add(withdrawal);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(withdrawal);
    }

    public async Task<Result<FundingWithdrawal>> PlaceAsync(
        Guid userId,
        Guid sessionId,
        Guid previewId,
        string? stepUpToken,
        string idempotencyKey,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stepUpToken))
        {
            return Result.Failure<FundingWithdrawal>(new Error(
                ErrorCodes.AuthStepUpInvalid,
                "A step-up grant is required to place a withdrawal.",
                ErrorType.Forbidden));
        }

        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            await LockUserAsync(userId, cancellationToken);

            var replay = await db.Withdrawals.AsNoTracking()
                .FirstOrDefaultAsync(
                    row => row.UserId == userId && row.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (replay is not null)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return Result.Success(replay);
            }

            var preview = await db.Withdrawals
                .FirstOrDefaultAsync(
                    row => row.UserId == userId && row.Id == previewId,
                    cancellationToken);
            if (preview is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(FundingResults.NotFound("withdrawal"));
            }

            var now = timeProvider.GetUtcNow();
            if (preview.State == WithdrawalState.Previewed && preview.ExpiresAt <= now)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(FundingResults.QuoteInvalid("expired"));
            }

            if (preview.State != WithdrawalState.Previewed || preview.PreviewUsed)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(FundingResults.QuoteInvalid("used"));
            }

            var option = ResolveCurrency(preview.Currency);
            if (option is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(FundingResults.NotAvailable("method_unsupported"));
            }

            var availabilityError = await CheckAvailabilityAsync(userId, option.Code, cancellationToken);
            if (availabilityError is not null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(availabilityError);
            }

            var beneficiary = await db.Beneficiaries.AsNoTracking()
                .FirstOrDefaultAsync(
                    row => row.UserId == userId && row.Id == preview.BeneficiaryId,
                    cancellationToken);
            if (beneficiary is null || beneficiary.State == BeneficiaryState.Removed)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(FundingResults.NotFound("beneficiary"));
            }

            if (!FundingBeneficiaryService.IsUsable(beneficiary, now))
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(FundingResults.BeneficiaryCoolingOff(
                    beneficiary.ActiveFrom ?? now));
            }

            var amountsError = await CheckPlacementAmountsAsync(
                userId, option, preview, now, cancellationToken);
            if (amountsError is not null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(amountsError);
            }

            var consumed = await stepUp.ConsumeAsync(
                userId,
                sessionId,
                StepUpPurposes.AuthorizeWithdrawal,
                preview.TermsHash,
                stepUpToken,
                cancellationToken);
            if (consumed.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(consumed.Error!);
            }

            var hold = await ledger.PostJournalAsync(
                FundingWithdrawalJournals.Hold(
                    preview.Id, userId, option.Code, preview.TotalDebited),
                cancellationToken);
            if (hold.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(hold.Error!);
            }

            var underReview = await RequiresReviewAsync(userId, option, preview, now, cancellationToken);
            var from = FundingText.ToDb(preview.State);
            preview.State = underReview ? WithdrawalState.UnderReview : WithdrawalState.Requested;
            preview.PreviewUsed = true;
            preview.IdempotencyKey = idempotencyKey;
            preview.HoldJournalId = hold.Value.Id;
            preview.RequestedAt = now;
            preview.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            events.Write(
                userId, FundingEventType.WithdrawalStateChanged, "withdrawal", preview.Id,
                from, FundingText.ToDb(preview.State), correlationId);
            if (!underReview)
            {
                await outbox.EnqueueAsync(
                    SubmitMessageType,
                    new { withdrawalId = preview.Id },
                    $"funding:submit:{preview.Id:N}",
                    cancellationToken);
            }

            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Funding withdrawal {WithdrawalId} placed.", preview.Id);
            }

            return Result.Success(preview);
        }
        catch (DbUpdateException exception) when (ownsScope
            && exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            })
        {
            await scope.RollbackAsync(cancellationToken);
            var existing = await db.Withdrawals.AsNoTracking()
                .FirstOrDefaultAsync(
                    row => row.UserId == userId && row.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return Result.Success(existing);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    public async Task<Result<FundingWithdrawal>> CancelAsync(
        Guid userId,
        Guid withdrawalId,
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
            await EnlistAsync(cancellationToken);
            await LockUserAsync(userId, cancellationToken);
            // Row lock serializes against the payout submission claim so a
            // cancel and a provider call can never interleave.
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM funding.withdrawals WHERE id = {withdrawalId} FOR UPDATE",
                cancellationToken);

            var row = await db.Withdrawals
                .FirstOrDefaultAsync(
                    candidate => candidate.UserId == userId && candidate.Id == withdrawalId,
                    cancellationToken);
            if (row is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(FundingResults.NotFound("withdrawal"));
            }

            if (row.State != WithdrawalState.Requested)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(new Error(
                    ErrorCodes.ResourceConflict,
                    "Only a requested withdrawal can be cancelled.",
                    ErrorType.Conflict));
            }

            var released = await ledger.PostJournalAsync(
                FundingWithdrawalJournals.Release(
                    row.Id, userId, row.Currency, row.TotalDebited),
                cancellationToken);
            if (released.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingWithdrawal>(released.Error!);
            }

            var from = FundingText.ToDb(row.State);
            row.State = WithdrawalState.Cancelled;
            row.ReleaseJournalId = released.Value.Id;
            row.CancelledAt = timeProvider.GetUtcNow();
            row.UpdatedAt = row.CancelledAt.Value;
            await db.SaveChangesAsync(cancellationToken);
            events.Write(
                userId, FundingEventType.WithdrawalStateChanged, "withdrawal", row.Id,
                from, FundingText.ToDb(WithdrawalState.Cancelled), correlationId);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return Result.Success(row);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    public async Task<Result<PageResult<FundingWithdrawal>>> ListAsync(
        Guid userId,
        WithdrawalState? state,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var hasCursor = TryDecodeCursor(cursor, out var cursorAt, out var cursorId);
        var query = db.Withdrawals.AsNoTracking()
            .Where(row => row.UserId == userId
                && (state == null || row.State == state));
        if (hasCursor)
        {
            query = query.Where(row =>
                row.CreatedAt < cursorAt
                || (row.CreatedAt == cursorAt && row.Id.CompareTo(cursorId) < 0));
        }

        var take = Math.Clamp(limit, 1, 100);
        var rows = await query
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > take;
        var page = rows.Take(take).ToList();
        var nextCursor = hasMore && page.Count > 0
            ? CursorCodec.Encode($"{page[^1].CreatedAt:O}|{page[^1].Id:N}")
            : null;
        return Result.Success(new PageResult<FundingWithdrawal>(page, nextCursor, hasMore, take));
    }

    public async Task<Result<FundingWithdrawal>> GetAsync(
        Guid userId,
        Guid withdrawalId,
        CancellationToken cancellationToken)
    {
        var row = await db.Withdrawals.AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.UserId == userId && candidate.Id == withdrawalId,
                cancellationToken);
        return row is null
            ? Result.Failure<FundingWithdrawal>(FundingResults.NotFound("withdrawal"))
            : Result.Success(row);
    }

    /// <summary>
    /// Cash withdrawable now: the user's available balance minus deposits
    /// credited inside the fresh-deposit lock window, never below zero.
    /// </summary>
    internal async Task<decimal> WithdrawableAsync(
        Guid userId,
        string currency,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var available = await AvailableAsync(userId, currency, cancellationToken);
        var locked = await LockedAsync(userId, currency, now, cancellationToken);
        return FundingMath.WithdrawableAfterFreshDepositLock(available, locked);
    }

    private async Task<decimal> AvailableAsync(
        Guid userId,
        string currency,
        CancellationToken cancellationToken)
    {
        var balances = await ledger.GetBalancesAsync(userId, Book.Real, cancellationToken);
        return balances.IsSuccess
            ? balances.Value
                .Where(balance => balance.AssetCode == currency)
                .Sum(balance => balance.Available)
            : 0m;
    }

    private async Task<decimal> LockedAsync(
        Guid userId,
        string currency,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var lockStart = now - TimeSpan.FromHours((double)options.Value.FreshDepositLockHours);
        return await db.Deposits.AsNoTracking()
            .Where(deposit => deposit.UserId == userId
                && deposit.Currency == currency
                && deposit.State == DepositState.Credited
                && deposit.CreditedAt > lockStart)
            .SumAsync(deposit => (decimal?)deposit.Amount, cancellationToken) ?? 0m;
    }

    private async Task<Error?> CheckAvailabilityAsync(
        Guid userId,
        string currency,
        CancellationToken cancellationToken)
    {
        var paused = options.Value.ManualPauses.Any(
            pause => string.Equals(pause.Currency, currency, StringComparison.Ordinal))
            || await db.Pauses.AsNoTracking()
                .AnyAsync(
                    pause => pause.Currency == currency
                        && pause.ConsecutiveShortfalls >= options.Value.ReservesShortfallThreshold,
                    cancellationToken);
        if (paused)
        {
            return FundingResults.NotAvailable("paused");
        }

        var restricted = await db.Restrictions.AsNoTracking()
            .AnyAsync(
                restriction => restriction.UserId == userId && restriction.ClearedAt == null,
                cancellationToken);
        return restricted ? FundingResults.NotAvailable("restricted") : null;
    }

    private async Task<Result<(decimal Receive, decimal Fee, decimal Total)>> ComputeAmountsAsync(
        Guid userId,
        FundingCurrencyOption option,
        string? receiveAmountText,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var withdrawable = await WithdrawableAsync(userId, option.Code, now, cancellationToken);
        decimal receive;
        if (string.IsNullOrWhiteSpace(receiveAmountText))
        {
            // "All withdrawable": solve receive + fee = withdrawable, capped
            // at the per-withdrawal maximum receive amount.
            var unit = Unit(option.Scale);
            receive = FundingMath.RoundDown(
                (withdrawable - option.WithdrawalFlatFee) / (1m + option.WithdrawalFeeBps / 10_000m),
                option.Scale);
            if (receive > option.WithdrawalMaximum)
            {
                receive = option.WithdrawalMaximum;
            }

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var (fee, total) = Fees(option, receive);
                if (total <= withdrawable || receive - unit < 0m)
                {
                    break;
                }

                receive -= unit;
            }
        }
        else
        {
            if (!decimal.TryParse(
                    receiveAmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out receive)
                || receive <= 0m)
            {
                return Result.Failure<(decimal, decimal, decimal)>(new Error(
                    ErrorCodes.ValidationFailed,
                    "The receive amount must be a positive number.",
                    ErrorType.Validation));
            }

            if (receive != decimal.Round(receive, option.Scale))
            {
                return Result.Failure<(decimal, decimal, decimal)>(new Error(
                    ErrorCodes.ValidationFailed,
                    $"The receive amount has more than {option.Scale} decimal places.",
                    ErrorType.Validation));
            }
        }

        var (feeAmount, totalDebited) = Fees(option, receive);
        if (receive < option.WithdrawalMinimum)
        {
            return Result.Failure<(decimal, decimal, decimal)>(FundingResults.LimitExceeded("below_minimum"));
        }

        if (receive > option.WithdrawalMaximum)
        {
            return Result.Failure<(decimal, decimal, decimal)>(FundingResults.LimitExceeded("above_maximum"));
        }

        var available = await AvailableAsync(userId, option.Code, cancellationToken);
        if (totalDebited > available)
        {
            return Result.Failure<(decimal, decimal, decimal)>(new Error(
                ErrorCodes.LedgerInsufficientFunds,
                "The available balance does not cover the withdrawal.",
                ErrorType.BusinessRule));
        }

        if (totalDebited > withdrawable)
        {
            return Result.Failure<(decimal, decimal, decimal)>(FundingResults.LimitExceeded(
                "fresh_deposit_lock"));
        }

        var capsError = await CheckRollingCapsAsync(
            userId, option, totalDebited, now, cancellationToken);
        if (capsError is not null)
        {
            return Result.Failure<(decimal, decimal, decimal)>(capsError);
        }

        return Result.Success((receive, feeAmount, totalDebited));
    }

    private async Task<Error?> CheckPlacementAmountsAsync(
        Guid userId,
        FundingCurrencyOption option,
        FundingWithdrawal preview,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var available = await AvailableAsync(userId, option.Code, cancellationToken);
        if (preview.TotalDebited > available)
        {
            return new Error(
                ErrorCodes.LedgerInsufficientFunds,
                "The available balance does not cover the withdrawal.",
                ErrorType.BusinessRule);
        }

        var withdrawable = await WithdrawableAsync(userId, option.Code, now, cancellationToken);
        if (preview.TotalDebited > withdrawable)
        {
            // The fresh-deposit lock tightened or a deposit reversed since
            // the preview; the hold would overdraw locked funds.
            return FundingResults.LimitExceeded("fresh_deposit_lock");
        }

        return await CheckRollingCapsAsync(userId, option, preview.TotalDebited, now, cancellationToken);
    }

    private async Task<Error?> CheckRollingCapsAsync(
        Guid userId,
        FundingCurrencyOption option,
        decimal additionalTotal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var dayStart = now - TimeSpan.FromHours(24);
        var monthStart = now - TimeSpan.FromDays(30);
        var committed = db.Withdrawals.AsNoTracking()
            .Where(row => row.UserId == userId
                && row.Currency == option.Code
                && row.State != WithdrawalState.Previewed
                && row.State != WithdrawalState.Failed
                && row.State != WithdrawalState.Cancelled);
        var dayUsed = await committed
            .Where(row => row.CreatedAt > dayStart)
            .SumAsync(row => (decimal?)row.TotalDebited, cancellationToken) ?? 0m;
        if (dayUsed + additionalTotal > option.WithdrawalDaily24Maximum)
        {
            return FundingResults.LimitExceeded("daily_24h_cap");
        }

        var monthUsed = await committed
            .Where(row => row.CreatedAt > monthStart)
            .SumAsync(row => (decimal?)row.TotalDebited, cancellationToken) ?? 0m;
        return monthUsed + additionalTotal > option.WithdrawalDaily30Maximum
            ? FundingResults.LimitExceeded("daily_30d_cap")
            : null;
    }

    private async Task<bool> RequiresReviewAsync(
        Guid userId,
        FundingCurrencyOption option,
        FundingWithdrawal preview,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (preview.ReceiveAmount > option.ReviewThresholdValue)
        {
            return true;
        }

        var snapshot = await users.FindAsync(userId, cancellationToken);
        if (snapshot is null)
        {
            return true;
        }

        var minAge = TimeSpan.FromHours((double)options.Value.ReviewMinAccountAgeHours);
        return minAge > TimeSpan.Zero && now - snapshot.CreatedAt < minAge;
    }

    private static (decimal Fee, decimal Total) Fees(FundingCurrencyOption option, decimal receive)
    {
        var fee = option.WithdrawalFlatFee
            + FundingMath.RoundHalfUp(receive * option.WithdrawalFeeBps / 10_000m, option.Scale);
        return (fee, receive + fee);
    }

    private static decimal Unit(int scale)
    {
        var unit = 1m;
        for (var index = 0; index < scale; index++)
        {
            unit /= 10m;
        }

        return unit;
    }

    private FundingCurrencyOption? ResolveCurrency(string currency)
    {
        var code = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        return options.Value.Currencies.FirstOrDefault(
            candidate => string.Equals(candidate.Code, code, StringComparison.Ordinal));
    }

    private async Task EnlistAsync(CancellationToken cancellationToken)
    {
        if (scope.Transaction is not null)
        {
            await db.Database.UseTransactionAsync(scope.Transaction, cancellationToken);
        }
    }

    private async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
        => await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('funding'), hashtext({userId.ToString("D")}))",
            cancellationToken);

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

    private static bool TryDecodeCursor(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = Guid.Empty;
        if (cursor is null || !CursorCodec.TryDecode(cursor, out var payload) || payload is null)
        {
            return false;
        }

        var separator = payload.IndexOf('|', StringComparison.Ordinal);
        return separator > 0
            && DateTimeOffset.TryParse(
                payload[..separator], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out createdAt)
            && Guid.TryParse(payload[(separator + 1)..], out id);
    }
}

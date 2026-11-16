using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Persistence;
using AskVest.Modules.Funding.Persistence.Entities;
using AskVest.Modules.Funding.Providers;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// The single deposit pipeline shared by the signed webhook and the polling
/// fallback. A deposit is stored once by (provider key, provider deposit
/// id); credit happens only on the provider's final completed state after
/// payer, limit, currency and restriction checks. Money that cannot be
/// credited safely is quarantined for an operator, never refused silently.
/// </summary>
internal sealed class FundingDepositService(
    FundingDbContext db,
    FundingProviderRegistry providers,
    ILedger ledger,
    IOptions<FundingOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    FundingEventWriter events,
    ILogger<FundingDepositService> logger)
{
    public const string DepositCreditedMessageType = "FundingDepositCredited";

    public const string DepositQuarantinedMessageType = "FundingDepositQuarantined";

    /// <summary>
    /// Applies one provider deposit report. Safe to call repeatedly: the
    /// unique provider key plus deposit id makes storage exactly once and
    /// state transitions are guarded.
    /// </summary>
    public async Task ApplyReportAsync(
        string providerKey,
        FundingDepositItem item,
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
            var row = await db.Deposits
                .FirstOrDefaultAsync(
                    deposit => deposit.ProviderKey == providerKey
                        && deposit.ProviderDepositId == item.ProviderDepositId,
                    cancellationToken);
            if (row is null)
            {
                row = await InsertAsync(providerKey, item, cancellationToken);
            }

            await ApplyStateAsync(row, providerKey, item, correlationId, ownsScope, cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task<FundingDeposit> InsertAsync(
        string providerKey,
        FundingDepositItem item,
        CancellationToken cancellationToken)
    {
        var account = await db.FundingAccounts.AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.ProviderKey == providerKey
                    && candidate.ProviderReference == item.AccountReference,
                cancellationToken);
        var now = timeProvider.GetUtcNow();
        var createdAt = item.ObservedAt == DateTimeOffset.MinValue || item.ObservedAt > now
            ? now
            : item.ObservedAt;
        var id = Guid.CreateVersion7();

        // ON CONFLICT keeps the transaction usable when a concurrent writer
        // wins the unique key; EF inserts would poison it with 23505.
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO funding.deposits
                (id, provider_key, provider_deposit_id, user_id, currency, amount,
                 provider_fee, payer_verified, state, shortfall, created_at, updated_at)
            VALUES
                ({id}, {providerKey}, {item.ProviderDepositId}, {(account?.UserId ?? Guid.Empty)},
                 {item.Currency}, {item.Amount}, {item.ProviderFee}, {item.PayerVerified},
                 'pending', 0, {createdAt}, {now})
            ON CONFLICT (provider_key, provider_deposit_id) DO NOTHING
            """,
            cancellationToken);

        return await db.Deposits
            .FirstAsync(
                deposit => deposit.ProviderKey == providerKey
                    && deposit.ProviderDepositId == item.ProviderDepositId,
                cancellationToken);
    }

    private async Task ApplyStateAsync(
        FundingDeposit row,
        string providerKey,
        FundingDepositItem item,
        string? correlationId,
        bool ownsScope,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (row.State == DepositState.Pending && row.UserId == Guid.Empty)
        {
            // The funding account may have been created after the deposit
            // was first seen; re-resolve on every report.
            var account = await db.FundingAccounts.AsNoTracking()
                .FirstOrDefaultAsync(
                    candidate => candidate.ProviderKey == providerKey
                        && candidate.ProviderReference == item.AccountReference,
                    cancellationToken);
            if (account is not null)
            {
                row.UserId = account.UserId;
            }
        }

        switch (row.State)
        {
            case DepositState.Pending:
                row.Amount = item.Amount;
                row.ProviderFee = item.ProviderFee;
                row.PayerVerified = item.PayerVerified;
                row.UpdatedAt = now;
                if (row.UserId == Guid.Empty && item.State != FundingDepositReportState.Pending)
                {
                    // Money arrived for nobody: store it ignored for operators.
                    await IgnoreAsync(row, "account_unknown", correlationId, ownsScope, cancellationToken);
                    return;
                }

                switch (item.State)
                {
                    case FundingDepositReportState.Pending:
                        await db.SaveChangesAsync(cancellationToken);
                        return;
                    case FundingDepositReportState.Failed:
                        await FailAsync(row, "provider_failed", correlationId, ownsScope, cancellationToken);
                        return;
                    case FundingDepositReportState.Reversed:
                        await FailAsync(row, "provider_reversed", correlationId, ownsScope, cancellationToken);
                        return;
                    case FundingDepositReportState.Completed:
                        await TryCreditAsync(row, providerKey, correlationId, ownsScope, cancellationToken);
                        return;
                }

                return;
            case DepositState.Credited when item.State == FundingDepositReportState.Reversed:
                await ReverseAsync(row, correlationId, ownsScope, cancellationToken);
                return;
            default:
                // Quarantined, failed, reversed and ignored rows are terminal
                // for automatic processing; operators resolve quarantine.
                return;
        }
    }

    private async Task TryCreditAsync(
        FundingDeposit row,
        string providerKey,
        string? correlationId,
        bool ownsScope,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var currency = options.Value.Currencies.FirstOrDefault(
            candidate => string.Equals(candidate.Code, row.Currency, StringComparison.Ordinal));
        if (currency is null)
        {
            await QuarantineAsync(row, "currency_unsupported", correlationId, ownsScope, cancellationToken);
            return;
        }

        if (!row.PayerVerified)
        {
            await QuarantineAsync(row, "payer_unverified", correlationId, ownsScope, cancellationToken);
            return;
        }

        if (row.Amount < currency.DepositMinimum || row.Amount > currency.DepositMaximum)
        {
            await QuarantineAsync(row, "amount_out_of_range", correlationId, ownsScope, cancellationToken);
            return;
        }

        var dayAgo = now.AddHours(-24);
        var creditedToday = await db.Deposits.AsNoTracking()
            .Where(deposit => deposit.UserId == row.UserId
                && deposit.Currency == row.Currency
                && deposit.State == DepositState.Credited
                && deposit.CreditedAt > dayAgo)
            .SumAsync(deposit => deposit.Amount, cancellationToken);
        if (creditedToday + row.Amount > currency.DepositDailyMaximum)
        {
            await QuarantineAsync(row, "daily_limit", correlationId, ownsScope, cancellationToken);
            return;
        }

        var restricted = await db.Restrictions.AsNoTracking()
            .AnyAsync(
                restriction => restriction.UserId == row.UserId && restriction.ClearedAt == null,
                cancellationToken);
        if (restricted)
        {
            await QuarantineAsync(row, "restricted", correlationId, ownsScope, cancellationToken);
            return;
        }

        var registered = await ledger.RegisterAssetAsync(
            row.Currency, AssetKind.Fiat, currency.Scale, cancellationToken);
        if (registered.IsFailure)
        {
            await QuarantineAsync(row, "asset_registration_failed", correlationId, ownsScope, cancellationToken);
            return;
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
            if (ownsScope)
            {
                await RollbackAsync(true, cancellationToken);
                logger.LogWarning("Deposit {DepositId} credit failed with {ErrorCode}.", row.Id, journal.Error!.Code);
                return;
            }

            // Inside a caller-owned scope the whole unit must fail so the
            // event is retried rather than half-applied.
            throw new InvalidOperationException($"Deposit credit journal failed: {journal.Error!.Code}");
        }

        var from = FundingText.ToDb(row.State);
        row.State = DepositState.Credited;
        row.CreditJournalId = journal.Value.Id;
        row.CreditedAt = now;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        events.Write(
            row.UserId, FundingEventType.DepositStateChanged, "deposit", row.Id,
            from, FundingText.ToDb(DepositState.Credited), correlationId);
        await outbox.EnqueueAsync(
            DepositCreditedMessageType,
            new { depositId = row.Id },
            $"funding:deposit:{row.Id:N}",
            cancellationToken);
        await CommitIfOwnerAsync(ownsScope, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Deposit {DepositId} credited.", row.Id);
        }
    }

    /// <summary>
    /// A provider reversal debits the user's available balance up to the
    /// credited amount, records any unrecoverable shortfall, marks the
    /// deposit reversed and places a funding restriction that blocks new
    /// cash withdrawals until an operator clears it.
    /// </summary>
    private async Task ReverseAsync(
        FundingDeposit row,
        string? correlationId,
        bool ownsScope,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var balanceResult = await ledger.GetBalancesAsync(row.UserId, Book.Real, cancellationToken);
        if (balanceResult.IsFailure)
        {
            if (ownsScope)
            {
                await RollbackAsync(true, cancellationToken);
                logger.LogWarning("Deposit {DepositId} reversal ledger read failed with {ErrorCode}.", row.Id, balanceResult.Error!.Code);
                return;
            }

            throw new InvalidOperationException($"Deposit reversal balance read failed: {balanceResult.Error!.Code}");
        }

        var available = balanceResult.Value
            .Where(balance => balance.AssetCode == row.Currency)
            .Sum(balance => balance.Available);
        var reversible = Math.Min(row.Amount, Math.Max(0m, available));

        Guid? reversalJournalId = null;
        if (reversible > 0m)
        {
            var journal = await ledger.PostJournalAsync(new PostJournalRequest(
                $"funding:deposit:{row.Id:N}:reversal",
                Book.Real,
                LedgerReasonCodes.DepositReversal,
                "funding_deposit",
                row.Id,
                [
                    new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, row.UserId), row.Currency, -reversible),
                    new LedgerLine(LedgerAccounts.SystemClearing(Book.Real), row.Currency, reversible),
                ]), cancellationToken);
            if (journal.IsFailure)
            {
                if (ownsScope)
                {
                    await RollbackAsync(true, cancellationToken);
                    logger.LogWarning("Deposit {DepositId} reversal failed with {ErrorCode}.", row.Id, journal.Error!.Code);
                    return;
                }

                throw new InvalidOperationException($"Deposit reversal journal failed: {journal.Error!.Code}");
            }

            reversalJournalId = journal.Value.Id;
        }

        var from = FundingText.ToDb(row.State);
        row.State = DepositState.Reversed;
        row.ReversalJournalId = reversalJournalId;
        row.Shortfall = row.Amount - reversible;
        row.ReversedAt = now;
        row.UpdatedAt = now;
        db.Restrictions.Add(new FundingRestriction
        {
            Id = Guid.CreateVersion7(),
            UserId = row.UserId,
            ReasonCode = "deposit_reversal",
            Since = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        events.Write(
            row.UserId, FundingEventType.DepositStateChanged, "deposit", row.Id,
            from, FundingText.ToDb(DepositState.Reversed), correlationId);
        events.Write(
            row.UserId, FundingEventType.RestrictionAdded, "deposit", row.Id,
            null, "deposit_reversal", correlationId);
        await CommitIfOwnerAsync(ownsScope, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Deposit {DepositId} reversed.", row.Id);
        }
    }

    private async Task QuarantineAsync(
        FundingDeposit row,
        string detailCode,
        string? correlationId,
        bool ownsScope,
        CancellationToken cancellationToken)
    {
        var from = FundingText.ToDb(row.State);
        row.State = DepositState.Quarantined;
        row.DetailCode = detailCode;
        row.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        events.Write(
            row.UserId, FundingEventType.DepositStateChanged, "deposit", row.Id,
            from, FundingText.ToDb(DepositState.Quarantined), correlationId, detailCode);
        await outbox.EnqueueAsync(
            DepositQuarantinedMessageType,
            new { depositId = row.Id },
            $"funding:deposit:{row.Id:N}",
            cancellationToken);
        await CommitIfOwnerAsync(ownsScope, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Deposit {DepositId} quarantined: {DetailCode}.", row.Id, detailCode);
        }
    }

    private async Task IgnoreAsync(
        FundingDeposit row,
        string detailCode,
        string? correlationId,
        bool ownsScope,
        CancellationToken cancellationToken)
    {
        var from = FundingText.ToDb(row.State);
        row.State = DepositState.Ignored;
        row.DetailCode = detailCode;
        row.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        if (row.UserId != Guid.Empty)
        {
            events.Write(
                row.UserId, FundingEventType.DepositStateChanged, "deposit", row.Id,
                from, FundingText.ToDb(DepositState.Ignored), correlationId, detailCode);
        }

        await CommitIfOwnerAsync(ownsScope, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Deposit {DepositId} ignored: {DetailCode}.", row.Id, detailCode);
        }
    }

    private async Task FailAsync(
        FundingDeposit row,
        string detailCode,
        string? correlationId,
        bool ownsScope,
        CancellationToken cancellationToken)
    {
        var from = FundingText.ToDb(row.State);
        row.State = DepositState.Failed;
        row.DetailCode = detailCode;
        row.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        if (row.UserId != Guid.Empty)
        {
            events.Write(
                row.UserId, FundingEventType.DepositStateChanged, "deposit", row.Id,
                from, FundingText.ToDb(DepositState.Failed), correlationId, detailCode);
        }

        await CommitIfOwnerAsync(ownsScope, cancellationToken);
    }

    /// <summary>
    /// One polling cycle across every configured provider. The stored cursor
    /// resumes a cycle interrupted mid-scan; a completed pass resets it so
    /// the next cycle rescans recent deposits, which deduplication absorbs.
    /// </summary>
    public async Task ProcessDepositsAsync(CancellationToken cancellationToken)
    {
        foreach (var providerKey in providers.Keys)
        {
            if (!providers.TryGet(providerKey, out var provider) || provider is null)
            {
                continue;
            }

            var cursor = await ReadCursorAsync(providerKey, cancellationToken);
            try
            {
                var completed = true;
                do
                {
                    var page = await provider.ListDepositsAsync(cursor, 100, cancellationToken);
                    foreach (var item in page.Items)
                    {
                        await ApplyReportAsync(providerKey, item, null, cancellationToken);
                    }

                    cursor = page.NextCursor;
                    await SaveCursorAsync(providerKey, cursor, cancellationToken);
                    completed = cursor is null;
                }
                while (cursor is not null && !cancellationToken.IsCancellationRequested);

                if (completed)
                {
                    await SaveCursorAsync(providerKey, null, cancellationToken);
                }
            }
            catch (FundingProviderException exception)
            {
                logger.LogWarning(
                    "Deposit polling failed for provider {ProviderKey}: {Kind}.",
                    providerKey, exception.Kind);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Deposit polling failed unexpectedly for provider {ProviderKey}.", providerKey);
            }
        }
    }

    private async Task<string?> ReadCursorAsync(string providerKey, CancellationToken cancellationToken)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            var cursor = await db.PollCursors.AsNoTracking()
                .Where(row => row.ProviderKey == providerKey)
                .Select(row => row.Cursor)
                .FirstOrDefaultAsync(cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return cursor;
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task SaveCursorAsync(string providerKey, string? cursor, CancellationToken cancellationToken)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            var row = await db.PollCursors
                .FirstOrDefaultAsync(candidate => candidate.ProviderKey == providerKey, cancellationToken);
            if (row is null)
            {
                db.PollCursors.Add(new FundingPollCursor
                {
                    ProviderKey = providerKey,
                    Cursor = cursor,
                    UpdatedAt = timeProvider.GetUtcNow(),
                });
            }
            else
            {
                row.Cursor = cursor;
                row.UpdatedAt = timeProvider.GetUtcNow();
            }

            await db.SaveChangesAsync(cancellationToken);
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

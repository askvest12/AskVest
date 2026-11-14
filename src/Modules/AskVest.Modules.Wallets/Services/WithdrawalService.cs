using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.MarketData.Contracts;
using AskVest.Modules.Wallets.Configuration;
using AskVest.Modules.Wallets.Custody;
using AskVest.Modules.Wallets.Domain;
using AskVest.Modules.Wallets.Persistence;
using AskVest.Modules.Wallets.Persistence.Entities;
using AskVest.Modules.Wallets.Screening;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets.Services;

/// <summary>
/// Withdrawal previews and placement. A preview freezes the terms the user
/// saw; placement re-validates everything under a per-user lock, consumes a
/// step-up grant bound to the terms hash, reserves the gross quantity in the
/// ledger, consumes the preview and enqueues submission - all in one
/// transaction.
/// </summary>
internal sealed class WithdrawalService(
    WalletsDbContext db,
    ICatalog catalog,
    IStepUpAuthorizer stepUp,
    IAddressScreeningProvider screening,
    ILedger ledger,
    IMarketData marketData,
    CustodyRegistry custody,
    AddressBookService addressBook,
    IOptions<WalletsOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    WalletEventWriter events)
{
    public const string SubmitMessageType = "WalletsWithdrawalSubmit";

    public async Task<Result<Withdrawal>> CreatePreviewAsync(
        Guid userId,
        Guid tokenId,
        Guid addressId,
        decimal grossQuantity,
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

            var validation = await ValidatePreviewCoreAsync(
                userId, tokenId, addressId, grossQuantity, cancellationToken);
            if (validation.Error is not null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<Withdrawal>(validation.Error);
            }

            var prepared = validation.Prepared!;
            var now = timeProvider.GetUtcNow();
            var withdrawal = new Withdrawal
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                TokenId = prepared!.Token.Id,
                AddressId = prepared.Address.Id,
                DestinationChainId = prepared.Address.ChainId,
                DestinationAddress = prepared.Address.CanonicalAddress,
                GrossQuantity = grossQuantity,
                Fee = prepared.Fee,
                NetQuantity = prepared.Net,
                State = WithdrawalState.Previewed,
                ExpiresAt = now + TimeSpan.FromSeconds(options.Value.PreviewLifetimeSeconds),
                IdempotencyKey = string.Empty,
                PreviewUsed = false,
                CreatedAt = now,
                UpdatedAt = now,
            };
            withdrawal.TermsHash = WithdrawalTermsHash.Compute(
                withdrawal.Id,
                withdrawal.TokenId,
                withdrawal.AddressId,
                withdrawal.DestinationChainId,
                withdrawal.DestinationAddress,
                withdrawal.GrossQuantity,
                withdrawal.Fee,
                withdrawal.NetQuantity,
                withdrawal.ExpiresAt);
            db.Withdrawals.Add(withdrawal);
            await db.SaveChangesAsync(cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return Result.Success(withdrawal);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    public async Task<Result<Withdrawal>> PlaceAsync(
        Guid userId,
        Guid sessionId,
        Guid previewId,
        string stepUpToken,
        string idempotencyKey,
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
                    row => row.Id == previewId && row.UserId == userId,
                    cancellationToken);
            if (preview is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<Withdrawal>(WalletsResults.NotFound("withdrawal"));
            }

            var now = timeProvider.GetUtcNow();
            if (preview.PreviewUsed || preview.State != WithdrawalState.Previewed)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<Withdrawal>(WalletsResults.PreviewInvalid("already_used"));
            }

            if (preview.ExpiresAt <= now)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<Withdrawal>(WalletsResults.PreviewInvalid("expired"));
            }

            // Re-validate every term against live state; the preview is a
            // convenience, never a promise.
            var validation = await ValidatePreviewCoreAsync(
                userId, preview.TokenId, preview.AddressId, preview.GrossQuantity,
                cancellationToken);
            if (validation.Error is not null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<Withdrawal>(validation.Error);
            }

            var prepared = validation.Prepared!;
            if (prepared.Fee != preview.Fee || prepared.Net != preview.NetQuantity)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<Withdrawal>(WalletsResults.PreviewInvalid("terms_changed"));
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
                return Result.Failure<Withdrawal>(consumed.Error!);
            }

            var hold = WithdrawalJournals.Hold(
                preview.Id, userId, prepared.Token.LedgerAssetCode, preview.GrossQuantity);
            var posted = await ledger.PostJournalAsync(hold, cancellationToken);
            if (posted.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<Withdrawal>(posted.Error!);
            }

            preview.State = WithdrawalState.Requested;
            preview.IdempotencyKey = idempotencyKey;
            preview.PreviewUsed = true;
            preview.HoldJournalId = posted.Value.Id;
            preview.RequestedAt = now;
            preview.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            events.Write(
                userId, WalletEventType.WithdrawalStateChanged, "withdrawal", preview.Id,
                WalletsText.ToDb(WithdrawalState.Previewed), WalletsText.ToDb(WithdrawalState.Requested),
                correlationId);
            await outbox.EnqueueAsync(
                SubmitMessageType,
                new { withdrawalId = preview.Id },
                $"wallets:submit:{preview.Id:N}",
                cancellationToken);
            await outbox.EnqueueAsync(
                "WalletsWithdrawalStateChanged",
                new { withdrawalId = preview.Id },
                $"wallets:state:{preview.Id:N}:requested",
                cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return Result.Success(preview);
        }
        catch (DbUpdateException exception) when (ownsScope
            && exception.InnerException is Npgsql.PostgresException
            {
                SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
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

    public async Task<Result<Withdrawal>> GetAsync(
        Guid userId,
        Guid withdrawalId,
        CancellationToken cancellationToken)
    {
        var row = await db.Withdrawals.AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id == withdrawalId && candidate.UserId == userId,
                cancellationToken);
        return row is null
            ? Result.Failure<Withdrawal>(WalletsResults.NotFound("withdrawal"))
            : Result.Success(row);
    }

    public async Task<Result<IReadOnlyList<Withdrawal>>> ListAsync(
        Guid userId,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await db.Withdrawals.AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<Withdrawal>>(rows);
    }

    public async Task<Result<Withdrawal>> CancelAsync(
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
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM wallets.withdrawals WHERE id = {withdrawalId} FOR UPDATE",
                cancellationToken);
            var row = await db.Withdrawals
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == withdrawalId && candidate.UserId == userId,
                    cancellationToken);
            if (row is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<Withdrawal>(WalletsResults.NotFound("withdrawal"));
            }

            if (!WithdrawalTransitions.CanTransition(row.State, WithdrawalState.Cancelled))
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<Withdrawal>(WalletsResults.PreviewInvalid("not_cancellable"));
            }

            if (row.HoldJournalId is { } holdJournalId)
            {
                var released = await ledger.ReverseJournalAsync(
                    WithdrawalJournals.ReleaseKey(row.Id), holdJournalId, cancellationToken);
                if (released.IsFailure)
                {
                    await RollbackAsync(ownsScope, cancellationToken);
                    return Result.Failure<Withdrawal>(released.Error!);
                }

                row.ReleaseJournalId = released.Value.Id;
            }

            var from = row.State;
            row.State = WithdrawalState.Cancelled;
            row.CancelledAt = timeProvider.GetUtcNow();
            row.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            events.Write(
                userId, WalletEventType.WithdrawalStateChanged, "withdrawal", row.Id,
                WalletsText.ToDb(from), WalletsText.ToDb(WithdrawalState.Cancelled), correlationId);
            await outbox.EnqueueAsync(
                "WalletsWithdrawalStateChanged",
                new { withdrawalId = row.Id },
                $"wallets:state:{row.Id:N}:cancelled",
                cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return Result.Success(row);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Shared validation for preview creation and placement re-checks.
    /// </summary>
    private async Task<PreviewValidation> ValidatePreviewCoreAsync(
        Guid userId,
        Guid tokenId,
        Guid addressId,
        decimal grossQuantity,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (await IsPausedAsync(userId, tokenId, cancellationToken))
        {
            return new PreviewValidation(WalletsResults.Unavailable(), null);
        }

        var token = await catalog.FindTokenAsync(tokenId, cancellationToken);
        if (token is null)
        {
            return new PreviewValidation(WalletsResults.WalletNotAvailable("token_unknown"), null);
        }

        if (!string.Equals(token.ExternalTransfers, "allowed", StringComparison.Ordinal))
        {
            return new PreviewValidation(WalletsResults.WalletNotAvailable("external_transfers_" + token.ExternalTransfers), null);
        }

        if (!custody.TryResolve(token.ChainId, out var provider))
        {
            return new PreviewValidation(WalletsResults.WalletNotAvailable("no_custody"), null);
        }

        var address = await addressBook.FindActiveAddressAsync(userId, addressId, cancellationToken);
        if (address is null)
        {
            return new PreviewValidation(
                addressId == Guid.Empty
                    ? WalletsResults.AddressInvalid("unknown")
                    : WalletsResults.CoolingOff(),
                null);
        }

        if (!string.Equals(address.ChainId, token.ChainId, StringComparison.Ordinal))
        {
            return new PreviewValidation(WalletsResults.AddressInvalid("chain_mismatch"), null);
        }

        var internalCheck = await provider.IsInternalAddressAsync(
            address.ChainId, address.CanonicalAddress, cancellationToken);
        if (internalCheck.IsFailure || internalCheck.Value)
        {
            return new PreviewValidation(WalletsResults.AddressInvalid("internal_address"), null);
        }

        var verdict = await screening.ScreenAsync(
            address.ChainId, address.CanonicalAddress, cancellationToken);
        if (verdict.Kind != ScreeningOutcomeKind.Clear)
        {
            return new PreviewValidation(
                verdict.Kind == ScreeningOutcomeKind.Hit
                    ? WalletsResults.AddressInvalid("screening_hit")
                    : WalletsResults.Unavailable(),
                null);
        }

        var fee = WalletFeeMath.FeeForToken(settings.WithdrawalFees, token.Id);
        var net = WalletFeeMath.NetQuantity(grossQuantity, fee, token.Decimals);
        if (net <= 0m)
        {
            return new PreviewValidation(WalletsResults.PreviewInvalid("fee_exceeds_gross"), null);
        }

        var reference = await marketData.GetLatestPriceAsync(token.InstrumentId, cancellationToken);
        if (reference is not { UsableForExecution: true, Price: > 0m })
        {
            return new PreviewValidation(WalletsResults.Unavailable(), null);
        }

        var netValueUsd = net * reference.Price.Value;
        var rolling = await db.Withdrawals.AsNoTracking()
            .Where(row => row.UserId == userId
                && WithdrawalTransitions.CountsTowardRollingTotal(row.State)
                && row.CreatedAt >= timeProvider.GetUtcNow() - TimeSpan.FromHours(24))
            .ToListAsync(cancellationToken);
        var usage = 0m;
        foreach (var group in rolling.GroupBy(row => row.TokenId))
        {
            var groupToken = await catalog.FindTokenAsync(group.Key, cancellationToken);
            if (groupToken is null)
            {
                continue;
            }

            var groupPrice = await marketData.GetLatestPriceAsync(groupToken.InstrumentId, cancellationToken);
            if (groupPrice is { UsableForExecution: true, Price: not null })
            {
                usage += group.Sum(row => row.NetQuantity) * groupPrice.Price.Value;
            }
        }

        var limits = new WithdrawalLimits(
            settings.WithdrawalMinQuantity,
            settings.MaxWithdrawalValueUsd,
            settings.Rolling24HourMaxValueUsd);
        var violation = WithdrawalLimitEvaluation.Evaluate(
            limits, new WithdrawalUsage(usage), net, netValueUsd);
        if (violation != WithdrawalLimitViolation.None)
        {
            return new PreviewValidation(WalletsResults.LimitExceeded(violation switch
            {
                WithdrawalLimitViolation.BelowMinimum => "below_minimum",
                WithdrawalLimitViolation.AboveMaximumValue => "above_maximum",
                _ => "rolling_limit",
            }), null);
        }

        return new PreviewValidation(null, new PreparedWithdrawal(token, address, fee, net));
    }

    /// <summary>True when an operator or reserves pause covers this scope.</summary>
    public async Task<bool> IsPausedAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken)
    {
        var chainId = (await catalog.FindTokenAsync(tokenId, cancellationToken))?.ChainId;
        if (chainId is null)
        {
            return false;
        }

        var manual = options.Value.ManualPauses.Any(pause =>
            string.Equals(pause.ChainId, chainId, StringComparison.Ordinal)
            && (pause.TokenId is null || string.Equals(pause.TokenId, tokenId.ToString("D"), StringComparison.Ordinal)));
        if (manual)
        {
            return true;
        }

        return await db.CustodyPauses.AsNoTracking()
            .AnyAsync(
                pause => pause.PausedAt != null
                    && pause.ChainId == chainId
                    && (pause.TokenId == null || pause.TokenId == tokenId),
                cancellationToken);
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
            $"SELECT pg_advisory_xact_lock(hashtext('wallets'), hashtext({userId.ToString("D")}))",
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

    private sealed record PreviewValidation(Error? Error, PreparedWithdrawal? Prepared);

    private sealed record PreparedWithdrawal(
        CatalogToken Token,
        WithdrawalAddress Address,
        decimal Fee,
        decimal Net);
}

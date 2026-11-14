using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Identity.Contracts;
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
/// The withdrawal address book. New addresses cool off before they can
/// receive withdrawals, every addition requires a step-up grant bound to the
/// address, and destinations are screened fail-closed through the provider
/// seam. Provider deposit addresses and AskVest's own issued addresses are
/// refused.
/// </summary>
internal sealed class AddressBookService(
    WalletsDbContext db,
    CustodyRegistry custody,
    IAddressScreeningProvider screening,
    ICatalog catalog,
    IStepUpAuthorizer stepUp,
    IOptions<WalletsOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    WalletEventWriter events)
{
    public const string AddressAddedMessageType = "WalletsAddressAdded";

    public async Task<Result<IReadOnlyList<WithdrawalAddress>>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var addresses = await db.WithdrawalAddresses.AsNoTracking()
            .Where(row => row.UserId == userId && row.State != WithdrawalAddressState.Removed)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);
        foreach (var address in addresses.Where(a => a.State == WithdrawalAddressState.CoolingOff))
        {
            if (address.ActiveFrom is { } activeFrom && activeFrom <= now)
            {
                address.State = WithdrawalAddressState.Active;
            }
        }

        return Result.Success<IReadOnlyList<WithdrawalAddress>>(addresses);
    }

    public async Task<Result<WithdrawalAddress>> AddAsync(
        Guid userId,
        Guid sessionId,
        string chainId,
        string address,
        string? label,
        string? stepUpToken,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stepUpToken))
        {
            return Result.Failure<WithdrawalAddress>(new Error(
                ErrorCodes.AuthStepUpInvalid,
                "A step-up grant is required to add a withdrawal address.",
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

            var chain = await catalog.FindChainAsync(chainId, cancellationToken);
            if (chain is null || chain.Status != "active")
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<WithdrawalAddress>(WalletsResults.WalletNotAvailable("chain_unknown"));
            }

            var canonical = await catalog.ValidateAddressAsync(chainId, address, cancellationToken);
            if (canonical.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<WithdrawalAddress>(
                    WalletsResults.AddressInvalid(canonical.Error!.Message));
            }

            var normalized = canonical.Value;
            var duplicate = await db.WithdrawalAddresses.AsNoTracking()
                .AnyAsync(
                    row => row.UserId == userId && row.ChainId == chainId
                        && row.CanonicalAddress == normalized
                        && row.State != WithdrawalAddressState.Removed,
                    cancellationToken);
            if (duplicate)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<WithdrawalAddress>(new Error(
                    ErrorCodes.ValidationFailed,
                    "The address is already in the address book.",
                    ErrorType.Validation));
            }

            var count = await db.WithdrawalAddresses
                .CountAsync(
                    row => row.UserId == userId && row.State != WithdrawalAddressState.Removed,
                    cancellationToken);
            if (count >= options.Value.AddressBookMaximum)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<WithdrawalAddress>(WalletsResults.LimitExceeded("address_book_full"));
            }

            if (custody.TryResolve(chainId, out var provider))
            {
                var internalCheck = await provider.IsInternalAddressAsync(chainId, normalized, cancellationToken);
                if (internalCheck.IsFailure || internalCheck.Value)
                {
                    await RollbackAsync(ownsScope, cancellationToken);
                    return Result.Failure<WithdrawalAddress>(
                        WalletsResults.AddressInvalid("internal_address"));
                }
            }

            // A withdrawal address must never be one of the user's own deposit
            // addresses on any chain.
            var ownDeposit = await db.WalletAddresses.AsNoTracking()
                .AnyAsync(
                    row => row.UserId == userId && row.Address == normalized,
                    cancellationToken);
            if (ownDeposit)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<WithdrawalAddress>(
                    WalletsResults.AddressInvalid("own_deposit_address"));
            }

            var verdict = await screening.ScreenAsync(chainId, normalized, cancellationToken);
            if (verdict.Kind != ScreeningOutcomeKind.Clear)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<WithdrawalAddress>(verdict.Kind == ScreeningOutcomeKind.Hit
                    ? new Error(
                        ErrorCodes.WalletAddressInvalid,
                        "The address did not pass screening.",
                        ErrorType.Validation)
                    : WalletsResults.Unavailable());
            }

            var consumed = await stepUp.ConsumeAsync(
                userId,
                sessionId,
                StepUpPurposes.ChangeWithdrawalAddress,
                normalized,
                stepUpToken,
                cancellationToken);
            if (consumed.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<WithdrawalAddress>(consumed.Error!);
            }

            var now = timeProvider.GetUtcNow();
            var coolingOff = TimeSpan.FromHours((double)options.Value.AddressCoolingOffHours);
            var row = new WithdrawalAddress
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                ChainId = chainId,
                CanonicalAddress = normalized,
                Label = label?.Trim() ?? string.Empty,
                State = coolingOff == TimeSpan.Zero
                    ? WithdrawalAddressState.Active
                    : WithdrawalAddressState.CoolingOff,
                ScreeningResult = verdict.Kind.ToString(),
                ScreeningReference = verdict.Reference,
                CreatedAt = now,
                ActiveFrom = coolingOff == TimeSpan.Zero ? now : now + coolingOff,
            };
            db.WithdrawalAddresses.Add(row);
            await db.SaveChangesAsync(cancellationToken);
            events.Write(
                userId, WalletEventType.WithdrawalAddressAdded, "withdrawal_address", row.Id,
                null, WalletsText.ToDb(row.State), correlationId);
            await outbox.EnqueueAsync(
                AddressAddedMessageType,
                new { addressId = row.Id },
                $"wallets:address:{row.Id:N}",
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

    public async Task<Result<WithdrawalAddress>> RemoveAsync(
        Guid userId,
        Guid addressId,
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
            var row = await db.WithdrawalAddresses
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == addressId && candidate.UserId == userId,
                    cancellationToken);
            if (row is null)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<WithdrawalAddress>(WalletsResults.NotFound("address"));
            }

            if (WithdrawalAddressTransitions.CanTransition(row.State, WithdrawalAddressState.Removed))
            {
                row.State = WithdrawalAddressState.Removed;
                row.RemovedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
            }

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
    /// Loads an active address book entry for withdrawal placement, promoting
    /// cooling-off rows whose window has elapsed.
    /// </summary>
    public async Task<WithdrawalAddress?> FindActiveAddressAsync(
        Guid userId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        var row = await db.WithdrawalAddresses
            .FirstOrDefaultAsync(
                candidate => candidate.Id == addressId && candidate.UserId == userId,
                cancellationToken);
        if (row is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (row.State == WithdrawalAddressState.CoolingOff
            && row.ActiveFrom is { } activeFrom
            && activeFrom <= now)
        {
            row.State = WithdrawalAddressState.Active;
            await db.SaveChangesAsync(cancellationToken);
        }

        return row.State == WithdrawalAddressState.Active ? row : null;
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
}

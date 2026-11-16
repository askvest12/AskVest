using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Wallets.Configuration;
using AskVest.Modules.Wallets.Custody;
using AskVest.Modules.Wallets.Domain;
using AskVest.Modules.Wallets.Persistence;
using AskVest.Modules.Wallets.Persistence.Entities;
using AskVest.Modules.Wallets.Screening;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets.Services;

/// <summary>
/// Deposit instructions and the deposit pipeline. Instructions come from the
/// custody provider and are cached as WalletAddress rows; deposits are
/// discovered by the background job, resolved to a user and token, screened,
/// and credited through the ledger exactly once.
/// </summary>
internal sealed class WalletService(
    WalletsDbContext db,
    CustodyRegistry custody,
    IAddressScreeningProvider screening,
    ICatalog catalog,
    ILedger ledger,
    ApprovedStablecoins approvedStablecoins,
    IOptions<WalletsOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    IOutbox outbox,
    WalletEventWriter events,
    ILogger<WalletService> logger)
{
    public const string DepositCreditedMessageType = "WalletsDepositCredited";

    public async Task<Result<IReadOnlyList<WalletAddress>>> ListWalletsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var addresses = await db.WalletAddresses.AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderBy(row => row.ChainId)
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<WalletAddress>>(addresses);
    }

    public async Task<Result<WalletAddress>> GetWalletAsync(
        Guid userId,
        string chainId,
        CancellationToken cancellationToken)
    {
        var address = await db.WalletAddresses.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.UserId == userId && row.ChainId == chainId,
                cancellationToken);
        if (address is not null)
        {
            return Result.Success(address);
        }

        return await EnsureInstructionAsync(userId, chainId, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<Deposit>>> ListDepositsAsync(
        Guid userId,
        int limit,
        CancellationToken cancellationToken)
    {
        var deposits = await db.Deposits.AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderByDescending(row => row.DetectedAt)
            .ThenByDescending(row => row.Id)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<Deposit>>(deposits);
    }

    /// <summary>
    /// Returns the deposit instruction for a user and chain, creating the
    /// WalletAddress row on first use. Chains without a custody route fail
    /// closed.
    /// </summary>
    public async Task<Result<WalletAddress>> EnsureInstructionAsync(
        Guid userId,
        string chainId,
        CancellationToken cancellationToken)
    {
        var existing = await db.WalletAddresses
            .FirstOrDefaultAsync(
                row => row.UserId == userId && row.ChainId == chainId,
                cancellationToken);
        if (existing is not null)
        {
            return Result.Success(existing);
        }

        if (!custody.TryResolve(chainId, out var provider))
        {
            return Result.Failure<WalletAddress>(WalletsResults.WalletNotAvailable("no_custody"));
        }

        var instruction = await provider.GetDepositInstructionAsync(
            userId.ToString("D"), chainId, cancellationToken);
        if (instruction.IsFailure)
        {
            return Result.Failure<WalletAddress>(instruction.Error!);
        }

        var address = new WalletAddress
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            ChainId = chainId,
            Address = instruction.Value.Address,
            Memo = instruction.Value.Memo ?? string.Empty,
            ProviderReference = instruction.Value.ProviderReference,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.WalletAddresses.Add(address);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(address);
    }

    /// <summary>
    /// One deposit polling cycle across every routed chain. Each discovered
    /// deposit is stored once by (provider key, provider deposit id) and then
    /// resolved; only final deposits past the dust floor can credit.
    /// </summary>
    public async Task ProcessDepositsAsync(CancellationToken cancellationToken)
    {
        foreach (var (chainId, providerName) in options.Value.Custody.RouteProviders)
        {
            if (!custody.TryResolve(chainId, out var provider))
            {
                continue;
            }

            string? cursor = null;
            do
            {
                var page = await provider.ListDepositsAsync(chainId, cursor, cancellationToken);
                if (page.IsFailure)
                {
                    logger.LogWarning(
                        "Deposit listing failed for chain {ChainId} provider {Provider}.",
                        chainId, providerName);
                    break;
                }

                foreach (var deposit in page.Value.Deposits)
                {
                    await ProcessDepositAsync(provider.ProviderName, deposit, cancellationToken);
                }

                var nextCursor = page.Value.NextCursor;
                if (string.Equals(nextCursor, cursor, StringComparison.Ordinal))
                {
                    // A provider that echoes its cursor would page forever.
                    break;
                }

                cursor = nextCursor;
            }
            while (cursor is not null && !cancellationToken.IsCancellationRequested);
        }
    }

    private async Task ProcessDepositAsync(
        string providerKey,
        CustodyDeposit deposit,
        CancellationToken cancellationToken)
    {
        var existing = await db.Deposits.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.ProviderKey == providerKey
                    && row.ProviderDepositId == deposit.ProviderDepositId,
                cancellationToken);
        if (existing is not null)
        {
            if (existing.State == DepositState.Pending && deposit.Finality == CustodyDepositFinality.Final)
            {
                await ResolveDepositAsync(existing.Id, deposit, cancellationToken);
            }

            return;
        }

        var row = new Deposit
        {
            Id = Guid.CreateVersion7(),
            ProviderKey = providerKey,
            ProviderDepositId = deposit.ProviderDepositId,
            UserId = Guid.Empty,
            ChainId = deposit.ChainId,
            TokenId = Guid.Empty,
            Quantity = deposit.Quantity,
            SenderAddress = deposit.SenderAddress,
            SettlementReference = deposit.SettlementReference,
            State = DepositState.Pending,
            DetectedAt = deposit.ObservedAt > timeProvider.GetUtcNow()
                ? timeProvider.GetUtcNow()
                : deposit.ObservedAt,
        };
        db.Deposits.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        if (deposit.Finality == CustodyDepositFinality.Final)
        {
            await ResolveDepositAsync(row.Id, deposit, cancellationToken);
        }
    }

    /// <summary>
    /// Resolves destination, token and screening for a pending deposit and
    /// either credits it, quarantines it, ignores it, or leaves it pending.
    /// Runs inside its own transaction scope.
    /// </summary>
    private async Task ResolveDepositAsync(
        Guid depositId,
        CustodyDeposit deposit,
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
                .FirstOrDefaultAsync(d => d.Id == depositId, cancellationToken);
            if (row is null || row.State != DepositState.Pending)
            {
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return;
            }

            var destination = await db.WalletAddresses.AsNoTracking()
                .FirstOrDefaultAsync(
                    address => address.ChainId == deposit.ChainId
                        && address.Address == deposit.DestinationAddress,
                    cancellationToken);
            if (destination is null)
            {
                await IgnoreAsync(row, "destination_unknown", ownsScope, cancellationToken);
                return;
            }

            // Approved stablecoins credit a cash asset; every other contract
            // must resolve to a listed catalog token. Both paths share the
            // same dust, screening, finality and exactly-once rules.
            string assetCode;
            int assetDecimals;
            AssetKind assetKind;
            decimal dust;
            CatalogToken? token = null;
            if (approvedStablecoins.TryResolve(
                    deposit.ChainId, deposit.TokenContractAddress, out var stablecoin)
                && stablecoin is not null)
            {
                assetCode = stablecoin.LedgerAssetCode;
                assetDecimals = stablecoin.Decimals;
                assetKind = AssetKind.Stablecoin;
                dust = stablecoin.DustMinimum ?? options.Value.DefaultDustMinimum;
            }
            else
            {
                var tokens = await catalog.ListListedTokensForChainAsync(deposit.ChainId, cancellationToken);
                token = tokens.FirstOrDefault(candidate =>
                    string.Equals(candidate.CanonicalAddress, deposit.TokenContractAddress, StringComparison.Ordinal));
                if (token is null)
                {
                    await IgnoreAsync(row, "token_unknown", ownsScope, cancellationToken);
                    return;
                }

                assetCode = token.LedgerAssetCode;
                assetDecimals = token.Decimals;
                assetKind = AssetKind.Token;
                dust = DustMinimumFor(token.Id);
            }

            if (deposit.Quantity < dust)
            {
                await IgnoreAsync(row, "dust", ownsScope, cancellationToken);
                return;
            }

            var verdict = await screening.ScreenAsync(
                deposit.ChainId, deposit.SenderAddress, cancellationToken);
            if (verdict.Kind == ScreeningOutcomeKind.Hit)
            {
                row.State = DepositState.Quarantined;
                row.DetailCode = "screening_hit";
                row.UserId = destination.UserId;
                row.TokenId = token?.Id ?? Guid.Empty;
                row.AssetCode = assetCode;
                await db.SaveChangesAsync(cancellationToken);
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Deposit {DepositId} quarantined.", row.Id);
                }

                return;
            }

            if (verdict.Kind == ScreeningOutcomeKind.Error)
            {
                // Fail closed: the deposit stays pending and is retried on the
                // next cycle; it is never credited on uncertainty.
                await CommitIfOwnerAsync(ownsScope, cancellationToken);
                return;
            }

            await CreditAsync(row, destination.UserId, token, assetCode, assetDecimals, assetKind, ownsScope, cancellationToken);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task CreditAsync(
        Deposit row,
        Guid userId,
        CatalogToken? token,
        string assetCode,
        int assetDecimals,
        AssetKind assetKind,
        bool ownsScope,
        CancellationToken cancellationToken)
    {
        var registered = await ledger.RegisterAssetAsync(
            assetCode, assetKind, assetDecimals, cancellationToken);
        if (registered.IsFailure)
        {
            row.DetailCode = "asset_registration_failed";
            await db.SaveChangesAsync(cancellationToken);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return;
        }

        var journal = await ledger.PostJournalAsync(new PostJournalRequest(
            $"wallets:deposit:{row.Id:N}:credit",
            Book.Real,
            LedgerReasonCodes.Deposit,
            "wallet_deposit",
            row.Id,
            [
                new LedgerLine(LedgerAccounts.SystemClearing(Book.Real), assetCode, -row.Quantity),
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, userId), assetCode, row.Quantity),
            ]), cancellationToken);
        if (journal.IsFailure)
        {
            await RollbackAsync(ownsScope, cancellationToken);
            logger.LogWarning("Deposit {DepositId} credit failed with {ErrorCode}.", row.Id, journal.Error!.Code);
            return;
        }

        var now = timeProvider.GetUtcNow();
        row.UserId = userId;
        row.TokenId = token?.Id ?? Guid.Empty;
        row.AssetCode = assetCode;
        row.State = DepositState.Credited;
        row.CreditJournalId = journal.Value.Id;
        row.CreditedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        events.Write(
            userId, WalletEventType.DepositCredited, "deposit", row.Id,
            null, WalletsText.ToDb(DepositState.Credited), null);
        await outbox.EnqueueAsync(
            DepositCreditedMessageType,
            new { depositId = row.Id },
            $"wallets:deposit:{row.Id:N}",
            cancellationToken);
        await CommitIfOwnerAsync(ownsScope, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Deposit {DepositId} credited.", row.Id);
        }
    }

    private async Task IgnoreAsync(
        Deposit row,
        string detailCode,
        bool ownsScope,
        CancellationToken cancellationToken)
    {
        row.State = DepositState.Ignored;
        row.DetailCode = detailCode;
        await db.SaveChangesAsync(cancellationToken);
        await CommitIfOwnerAsync(ownsScope, cancellationToken);
    }

    private decimal DustMinimumFor(Guid tokenId)
    {
        var settings = options.Value;
        if (settings.DustMinimums.TryGetValue(tokenId.ToString("D"), out var perToken))
        {
            return perToken;
        }

        return settings.DefaultDustMinimum;
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

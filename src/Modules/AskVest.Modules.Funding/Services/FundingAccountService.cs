using System.Globalization;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Persistence;
using AskVest.Modules.Funding.Persistence.Entities;
using AskVest.Modules.Funding.Providers;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// Funding methods and account instructions. A funding account binds a user
/// and currency to a provider reference; the instruction payload is stored
/// opaquely and returned verbatim, never parsed or logged.
/// </summary>
internal sealed class FundingAccountService(
    FundingDbContext db,
    FundingProviderRegistry providers,
    IComplianceEligibility eligibility,
    IOptions<FundingOptions> options,
    TimeProvider timeProvider,
    FundingEventWriter events)
{
    public async Task<Result<IReadOnlyList<FundingMethodView>>> ListMethodsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var country = await eligibility.GetEffectiveCountryAsync(userId, cancellationToken);
        var methods = options.Value.Currencies
            .Where(currency => country is not null
                && currency.Countries.Contains(country, StringComparer.Ordinal))
            .Select(currency => new FundingMethodView(
                currency.Code,
                currency.DepositMinimum,
                currency.DepositMaximum,
                currency.DepositDailyMaximum,
                currency.WithdrawalMinimum,
                currency.WithdrawalMaximum,
                currency.WithdrawalDaily24Maximum,
                currency.WithdrawalDaily30Maximum,
                currency.WithdrawalFlatFee,
                currency.WithdrawalFeeBps))
            .ToArray();
        return Result.Success<IReadOnlyList<FundingMethodView>>(methods);
    }

    public async Task<Result<IReadOnlyList<FundingAccount>>> ListAccountsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var accounts = await db.FundingAccounts.AsNoTracking()
            .Where(account => account.UserId == userId)
            .OrderBy(account => account.Currency)
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<FundingAccount>>(accounts);
    }

    /// <summary>
    /// Creates or returns the funding instruction for a user and currency.
    /// The provider call is idempotent; a concurrent create resolves to the
    /// stored row through the unique constraint.
    /// </summary>
    public async Task<Result<FundingAccount>> CreateAccountAsync(
        Guid userId,
        string currency,
        CancellationToken cancellationToken)
    {
        var code = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        var option = options.Value.Currencies.FirstOrDefault(
            candidate => string.Equals(candidate.Code, code, StringComparison.Ordinal));
        if (option is null)
        {
            return Result.Failure<FundingAccount>(
                FundingResults.NotAvailable("method_unsupported"));
        }

        var country = await eligibility.GetEffectiveCountryAsync(userId, cancellationToken);
        if (country is null || !option.Countries.Contains(country, StringComparer.Ordinal))
        {
            return Result.Failure<FundingAccount>(
                FundingResults.NotAvailable("method_unsupported_for_country"));
        }

        var existing = await db.FundingAccounts
            .FirstOrDefaultAsync(
                row => row.UserId == userId && row.Currency == code,
                cancellationToken);
        if (existing is not null)
        {
            return Result.Success(existing);
        }

        var provider = providers.Get(option.Provider);
        FundingInstruction instruction;
        try
        {
            instruction = await provider.GetOrCreateInstructionAsync(
                userId.ToString("D"), code, cancellationToken);
        }
        catch (FundingProviderException exception)
        {
            return Result.Failure<FundingAccount>(MapProviderFailure(exception));
        }

        var account = new FundingAccount
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Currency = code,
            ProviderKey = provider.Key,
            ProviderReference = instruction.ProviderReference,
            InstructionJson = instruction.InstructionJson,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.FundingAccounts.Add(account);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            db.Entry(account).State = EntityState.Detached;
            var winner = await db.FundingAccounts.AsNoTracking()
                .FirstAsync(
                    row => row.UserId == userId && row.Currency == code,
                    cancellationToken);
            return Result.Success(winner);
        }

        events.Write(
            userId, FundingEventType.FundingAccountCreated, "funding_account", account.Id,
            null, null, null);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(account);
    }

    public async Task<Result<PageResult<FundingDeposit>>> ListDepositsAsync(
        Guid userId,
        DepositState? state,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var hasCursor = TryDecodeCursor(cursor, out var cursorAt, out var cursorId);
        var query = db.Deposits.AsNoTracking()
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
        return Result.Success(new PageResult<FundingDeposit>(page, nextCursor, hasMore, take));
    }

    public async Task<Result<FundingDeposit>> GetDepositAsync(
        Guid userId,
        Guid depositId,
        CancellationToken cancellationToken)
    {
        var deposit = await db.Deposits.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.UserId == userId && row.Id == depositId,
                cancellationToken);
        return deposit is null
            ? Result.Failure<FundingDeposit>(FundingResults.NotFound("deposit"))
            : Result.Success(deposit);
    }

    internal static Error MapProviderFailure(FundingProviderException exception)
        => exception.Kind switch
        {
            FundingProviderFailureKind.Rejected => FundingResults.BeneficiaryInvalid(
                exception.ReasonCode ?? "provider_rejected"),
            FundingProviderFailureKind.RateLimited => FundingResults.Unavailable("provider_rate_limited"),
            _ => FundingResults.Unavailable("provider_error"),
        };

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

/// <summary>Method limits and fees exposed to the client for one currency.</summary>
internal sealed record FundingMethodView(
    string Currency,
    decimal DepositMinimum,
    decimal DepositMaximum,
    decimal DepositDailyMaximum,
    decimal WithdrawalMinimum,
    decimal WithdrawalMaximum,
    decimal WithdrawalDaily24Maximum,
    decimal WithdrawalDaily30Maximum,
    decimal WithdrawalFlatFee,
    int WithdrawalFeeBps);

using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Persistence;
using AskVest.Modules.Funding.Persistence.Entities;
using AskVest.Modules.Funding.Providers;
using AskVest.Modules.Ledger.Contracts;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// The fiat reserves check: per local currency the provider settlement
/// balance must cover what the ledger owes users (available plus held plus
/// fees). Only a shortfall counts; excess is logged because treasury sweeps
/// happen outside this system. Currencies with in-flight withdrawals skip
/// the run. Consecutive shortfalls pause cash withdrawals for the currency
/// until an operator clears the stored pause.
/// </summary>
internal sealed class FundingReservesService(
    FundingDbContext db,
    FundingProviderRegistry providers,
    ILedger ledger,
    IOptions<FundingOptions> options,
    TimeProvider timeProvider,
    FundingEventWriter events,
    ILogger<FundingReservesService> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var totals = await ledger.GetAssetTotalsAsync(Book.Real, cancellationToken);
        if (totals.IsFailure)
        {
            logger.LogWarning("Ledger asset totals unavailable for funding reserves check.");
            return;
        }

        var inFlight = await db.Withdrawals.AsNoTracking()
            .Where(row => row.State != WithdrawalState.Completed
                && row.State != WithdrawalState.Failed
                && row.State != WithdrawalState.Cancelled)
            .Select(row => row.Currency)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var currency in options.Value.Currencies)
        {
            if (inFlight.Contains(currency.Code, StringComparer.Ordinal))
            {
                continue;
            }

            var provider = providers.TryGet(currency.Provider, out var resolved) ? resolved : null;
            if (provider is null)
            {
                continue;
            }

            decimal held;
            try
            {
                held = await provider.GetSettlementBalanceAsync(currency.Code, cancellationToken);
            }
            catch (FundingProviderException exception)
            {
                logger.LogWarning(
                    "Settlement balance unavailable for a funding currency: {ReasonCode}.",
                    exception.ReasonCode ?? exception.Kind.ToString());
                continue;
            }

            var owed = totals.Value
                .Where(total => string.Equals(total.AssetCode, currency.Code, StringComparison.Ordinal))
                .Sum(total => total.Total);
            if (FundingMath.ReservesShortfall(owed, held) > 0m)
            {
                await RecordShortfallAsync(currency.Code, cancellationToken);
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("Funding reserves shortfall recorded for a currency.");
                }
            }
            else
            {
                if (owed < held && logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Funding reserves excess recorded for a currency.");
                }

                await RecordCoveredAsync(currency.Code, cancellationToken);
            }
        }
    }

    private async Task RecordShortfallAsync(string currency, CancellationToken cancellationToken)
    {
        var pause = await GetOrCreatePauseAsync(currency, cancellationToken);
        if (pause.ConsecutiveShortfalls >= options.Value.ReservesShortfallThreshold)
        {
            // Already paused: the stored pause stays until an operator lifts it.
            return;
        }

        pause.ConsecutiveShortfalls += 1;
        pause.UpdatedAt = timeProvider.GetUtcNow();
        if (pause.ConsecutiveShortfalls >= options.Value.ReservesShortfallThreshold)
        {
            pause.ReasonCode = "reserves_shortfall";
            pause.PausedAt ??= pause.UpdatedAt;
            events.Write(
                Guid.Empty, FundingEventType.PauseChanged, "funding_pause", pause.Id,
                null, "paused", null, pause.ReasonCode);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordCoveredAsync(string currency, CancellationToken cancellationToken)
    {
        var pause = await db.Pauses
            .FirstOrDefaultAsync(row => row.Currency == currency, cancellationToken);
        if (pause is null
            || pause.ConsecutiveShortfalls >= options.Value.ReservesShortfallThreshold)
        {
            // A matching run never lifts an active pause; only an operator does.
            return;
        }

        if (pause.ConsecutiveShortfalls > 0)
        {
            pause.ConsecutiveShortfalls = 0;
            pause.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<FundingPause> GetOrCreatePauseAsync(
        string currency,
        CancellationToken cancellationToken)
    {
        var pause = await db.Pauses
            .FirstOrDefaultAsync(row => row.Currency == currency, cancellationToken);
        if (pause is not null)
        {
            return pause;
        }

        pause = new FundingPause
        {
            Id = Guid.CreateVersion7(),
            Currency = currency,
            ReasonCode = string.Empty,
            ConsecutiveShortfalls = 0,
            CreatedAt = timeProvider.GetUtcNow(),
            UpdatedAt = timeProvider.GetUtcNow(),
        };
        db.Pauses.Add(pause);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            db.Entry(pause).State = EntityState.Detached;
            pause = await db.Pauses
                .FirstAsync(row => row.Currency == currency, cancellationToken);
        }

        return pause;
    }
}

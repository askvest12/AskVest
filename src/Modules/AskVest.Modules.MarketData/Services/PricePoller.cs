using System.Collections.Concurrent;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.MarketData.Configuration;
using AskVest.Modules.MarketData.Domain;
using AskVest.Modules.MarketData.Domain.Enums;
using AskVest.Modules.MarketData.Persistence;
using AskVest.Modules.MarketData.Persistence.Entities;
using AskVest.Modules.MarketData.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.MarketData.Services;

internal sealed class PricePoller(
    IServiceScopeFactory scopeFactory,
    IOptions<MarketDataOptions> options,
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    IMarketDataProvider provider,
    ILogger<PricePoller> logger) : BackgroundService
{
    internal const long AdvisoryLockKey = 2025110801L;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _unmappedLoggedAt = new(StringComparer.Ordinal);

    internal int ConsecutiveFailures { get; private set; }

    internal int UnmappedWarningsLogged { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Market data poll cycle failed unexpectedly.");
            }

            try
            {
                await Task.Delay(NextDelay(), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task<bool> RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection))
        {
            lockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            var acquired = await lockCommand.ExecuteScalarAsync(cancellationToken);
            if (acquired is not true)
            {
                return false;
            }
        }

        try
        {
            await PollAsync(cancellationToken);
            return true;
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            unlockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var catalog = services.GetRequiredService<ICatalog>();
        var db = services.GetRequiredService<MarketDataDbContext>();

        var instruments = await catalog.ListActiveInstrumentsAsync(cancellationToken);
        var mappings = instruments
            .Select(instrument => new InstrumentMapping(
                instrument,
                ProviderSymbols.For(instrument, settings)))
            .ToList();
        if (mappings.Count == 0)
        {
            ConsecutiveFailures = 0;
            return;
        }

        var capabilities = provider.Capabilities;
        var batchSize = Math.Max(1, Math.Min(settings.BatchSize, capabilities.MaxBatchSize));
        var callBudget = settings.RequestsPerMinute * settings.PollIntervalSeconds / 60;
        if (callBudget < 1)
        {
            ConsecutiveFailures++;
            logger.LogWarning("Market data poll budget allows no provider calls this cycle.");
            return;
        }

        var symbolToInstrument = mappings.ToDictionary(m => m.ProviderSymbol, m => m.Instrument, StringComparer.Ordinal);
        var stored = await LoadStoredAsync(db, mappings, cancellationToken);

        var stats = new PollStats { Instruments = mappings.Count };
        var batches = mappings.Chunk(batchSize).ToList();
        foreach (var batch in batches.Take(callBudget))
        {
            stats.BatchesAttempted++;
            var symbols = batch.Select(m => m.ProviderSymbol).ToList();
            var result = await FetchQuotesAsync(symbols, cancellationToken);
            if (result.Outcome != ProviderOutcome.Succeeded)
            {
                stats.BatchesFailed++;
                continue;
            }

            stats.BatchesSucceeded++;
            ApplyQuotes(db, result, symbolToInstrument, stored, capabilities, now, stats);
        }

        if (stats.BatchesFailed > 0)
        {
            ConsecutiveFailures++;
            logger.LogWarning(
                "Market data poll cycle had provider failures: {BatchesFailed} of {BatchesAttempted} batches failed.",
                stats.BatchesFailed,
                stats.BatchesAttempted);
        }
        else
        {
            ConsecutiveFailures = 0;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Market data poll cycle completed: {Instruments} instruments, {BatchesAttempted} batches, {QuotesApplied} quotes applied, {QuotesRejected} rejected, {QuotesHeld} held, {UnknownSymbols} unmapped.",
                stats.Instruments,
                stats.BatchesAttempted,
                stats.QuotesApplied,
                stats.QuotesRejected,
                stats.QuotesHeld,
                stats.UnknownSymbols);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ProviderQuotesResult> FetchQuotesAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ProviderTimeoutSeconds));

        var result = await provider.GetLatestQuotesAsync(symbols, timeout.Token);
        if (result.Outcome == ProviderOutcome.RateLimited && result.RetryAfter is { } retryAfter)
        {
            await Task.Delay(retryAfter, timeProvider, cancellationToken);
            result = await provider.GetLatestQuotesAsync(symbols, timeout.Token);
        }

        return result;
    }

    private void ApplyQuotes(
        MarketDataDbContext db,
        ProviderQuotesResult result,
        Dictionary<string, CatalogInstrument> symbolToInstrument,
        Dictionary<string, LatestPrice> stored,
        MarketDataCapabilities capabilities,
        DateTimeOffset now,
        PollStats stats)
    {
        var settings = options.Value;
        foreach (var quote in result.Quotes)
        {
            if (!symbolToInstrument.TryGetValue(quote.ProviderSymbol, out var instrument))
            {
                continue;
            }

            var rejection = QuoteValidator.Validate(
                quote,
                instrument.QuoteCurrency,
                capabilities.ReportsSession,
                settings.MaxFractionalDigits,
                now);
            if (rejection is not null)
            {
                stats.QuotesRejected++;
                logger.LogWarning(
                    "Rejected market data quote for instrument {InstrumentId}: {Reason}.",
                    instrument.Id,
                    rejection);
                continue;
            }

            var quoteSession = capabilities.ReportsSession ? quote.Session : "unknown";
            stored.TryGetValue(instrument.Id, out var row);
            if (row is not null && quote.AsOf < row.AsOf)
            {
                continue;
            }

            if (row is null)
            {
                stats.QuotesApplied++;
                db.LatestPrices.Add(new LatestPrice
                {
                    SubjectType = MarketDataVocabulary.SubjectInstrument,
                    SubjectId = instrument.Id,
                    Price = quote.Price,
                    Currency = quote.Currency,
                    AsOf = quote.AsOf,
                    ReceivedAt = now,
                    Session = quoteSession,
                    PreviousClose = quote.PreviousClose,
                    ProviderKey = provider.ProviderKey,
                    DelayMinutes = capabilities.DelayMinutes,
                    UpdatedAt = now,
                });
                continue;
            }

            var decision = OutlierGuard.Evaluate(
                row.Price,
                row.SuspectPrice,
                quote.Price,
                settings.MaxTickChangePercent);
            switch (decision)
            {
                case OutlierDecision.HeldAsSuspect:
                    stats.QuotesHeld++;
                    row.SuspectPrice = quote.Price;
                    row.SuspectAt = now;
                    row.UnmappedAt = null;
                    row.UpdatedAt = now;
                    logger.LogWarning(
                        "Held suspect market price for instrument {InstrumentId}.",
                        instrument.Id);
                    break;
                case OutlierDecision.ConfirmedSuspect:
                case OutlierDecision.DiscardedSuspect:
                    stats.QuotesApplied++;
                    ApplyAccepted(row, quote, quoteSession, capabilities, provider.ProviderKey, now);
                    logger.LogWarning(
                        "Market price suspect {Decision} for instrument {InstrumentId}.",
                        decision,
                        instrument.Id);
                    break;
                default:
                    stats.QuotesApplied++;
                    ApplyAccepted(row, quote, quoteSession, capabilities, provider.ProviderKey, now);
                    break;
            }
        }

        foreach (var unknownSymbol in result.UnknownSymbols)
        {
            if (!symbolToInstrument.TryGetValue(unknownSymbol, out var instrument))
            {
                continue;
            }

            stats.UnknownSymbols++;
            if (stored.TryGetValue(instrument.Id, out var row))
            {
                row.UnmappedAt = now;
                row.UpdatedAt = now;
            }

            var lastLogged = _unmappedLoggedAt.GetOrAdd(instrument.Id, DateTimeOffset.MinValue);
            if (now - lastLogged >= TimeSpan.FromHours(1))
            {
                _unmappedLoggedAt[instrument.Id] = now;
                UnmappedWarningsLogged++;
                logger.LogWarning(
                    "Market data provider does not know the symbol for instrument {InstrumentId}.",
                    instrument.Id);
            }
        }
    }

    private static void ApplyAccepted(
        LatestPrice row,
        ProviderQuote quote,
        string session,
        MarketDataCapabilities capabilities,
        string providerKey,
        DateTimeOffset now)
    {
        row.Price = quote.Price;
        row.Currency = quote.Currency;
        row.AsOf = quote.AsOf;
        row.ReceivedAt = now;
        row.Session = session;
        row.PreviousClose = quote.PreviousClose;
        row.ProviderKey = providerKey;
        row.DelayMinutes = capabilities.DelayMinutes;
        row.SuspectPrice = null;
        row.SuspectAt = null;
        row.UnmappedAt = null;
        row.UpdatedAt = now;
    }

    private static async Task<Dictionary<string, LatestPrice>> LoadStoredAsync(
        MarketDataDbContext db,
        IReadOnlyList<InstrumentMapping> mappings,
        CancellationToken cancellationToken)
    {
        var ids = mappings.Select(m => m.Instrument.Id).ToList();
        var rows = await db.LatestPrices
            .Where(p => p.SubjectType == MarketDataVocabulary.SubjectInstrument && ids.Contains(p.SubjectId))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(p => p.SubjectId, StringComparer.Ordinal);
    }

    internal TimeSpan NextDelay()
    {
        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        if (ConsecutiveFailures < 5)
        {
            return interval;
        }

        var multiplier = 1 << Math.Min(ConsecutiveFailures - 4, 10);
        var backed = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds * multiplier);
        return backed > MaxBackoff ? MaxBackoff : backed;
    }

    internal sealed class PollStats
    {
        public int Instruments { get; set; }
        public int BatchesAttempted { get; set; }
        public int BatchesSucceeded { get; set; }
        public int BatchesFailed { get; set; }
        public int QuotesApplied { get; set; }
        public int QuotesRejected { get; set; }
        public int QuotesHeld { get; set; }
        public int UnknownSymbols { get; set; }
    }

    private sealed record InstrumentMapping(CatalogInstrument Instrument, string ProviderSymbol);
}

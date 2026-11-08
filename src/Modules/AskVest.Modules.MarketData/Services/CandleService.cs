using System.Collections.Concurrent;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.MarketData.Configuration;
using AskVest.Modules.MarketData.Domain;
using AskVest.Modules.MarketData.Persistence;
using AskVest.Modules.MarketData.Persistence.Entities;
using AskVest.Modules.MarketData.Providers;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.MarketData.Services;

internal sealed class CandleService(
    MarketDataDbContext db,
    ICatalog catalog,
    IMarketDataProvider provider,
    IOptions<MarketDataOptions> options,
    TimeProvider timeProvider,
    ILogger<CandleService> logger,
    ConcurrentDictionary<string, SemaphoreSlim> inflight)
{
    private static readonly TimeSpan Interval15m = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Interval1h = TimeSpan.FromHours(1);
    private static readonly TimeSpan Interval1d = TimeSpan.FromDays(1);

    public async Task<Result<CandleResponse>> GetCandlesAsync(
        string instrumentId,
        string interval,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var intervalOptions = settings.CandleIntervals.FirstOrDefault(option => option.Interval == interval);
        if (intervalOptions is null)
        {
            return Invalid("interval", "The interval is not enabled.");
        }

        if (from > to)
        {
            return Invalid("to", "The range end must not be before the range start.");
        }

        if (to - from > TimeSpan.FromDays(intervalOptions.MaxRangeDays))
        {
            return Invalid("to", "The range exceeds the maximum for this interval.");
        }

        var step = StepFor(interval);
        var slots = SlotCount(from, to, step);
        if (slots > intervalOptions.MaxCandles)
        {
            return Invalid("to", "The range asks for more candles than allowed.");
        }

        var instrument = await catalog.FindInstrumentAsync(instrumentId, cancellationToken);
        if (instrument is null || !string.Equals(instrument.Status, "active", StringComparison.Ordinal))
        {
            return Result.Failure<CandleResponse>(new Error(
                ErrorCodes.ResourceNotFound,
                "The instrument was not found.",
                ErrorType.NotFound));
        }

        var key = $"{instrumentId}|{interval}|{from:O}|{to:O}";
        var gate = inflight.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadOrFetchAsync(instrument, intervalOptions, step, from, to, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Result<CandleResponse>> LoadOrFetchAsync(
        CatalogInstrument instrument,
        CandleIntervalOptions intervalOptions,
        TimeSpan step,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cached = await LoadCachedAsync(instrument.Id, intervalOptions.Interval, from, to, step, cancellationToken);
        var missing = MissingSlots(cached, from, to, step, intervalOptions, now);
        if (missing.Count == 0)
        {
            return Result.Success(BuildResponse(cached, from, to, step, now, false));
        }

        var symbol = ProviderSymbols.For(instrument, options.Value);
        var result = await FetchCandlesAsync(symbol, intervalOptions.Interval, from, to, cancellationToken);
        if (result.Outcome != ProviderOutcome.Succeeded)
        {
            if (cached.Count == 0)
            {
                logger.LogWarning(
                    "Market data provider failed for instrument {InstrumentId} candles; nothing cached.",
                    instrument.Id);
                return Result.Failure<CandleResponse>(new Error(
                    ErrorCodes.MarketDataUnavailable,
                    "Market data is temporarily unavailable.",
                    ErrorType.Unavailable));
            }

            logger.LogWarning(
                "Market data provider failed for instrument {InstrumentId} candles; serving {Count} cached rows.",
                instrument.Id,
                cached.Count);
            return Result.Success(BuildResponse(cached, from, to, step, now, true));
        }

        await StoreAsync(instrument.Id, intervalOptions.Interval, step, result.Candles, now, cancellationToken);
        var merged = await LoadCachedAsync(instrument.Id, intervalOptions.Interval, from, to, step, cancellationToken);
        return Result.Success(BuildResponse(merged, from, to, step, now, false));
    }

    private async Task<List<Candle>> LoadCachedAsync(
        string instrumentId,
        string interval,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan step,
        CancellationToken cancellationToken)
    {
        var rangeFrom = FloorTo(from, step);
        return await db.Candles
            .Where(candle => candle.SubjectType == MarketDataVocabulary.SubjectInstrument
                && candle.SubjectId == instrumentId
                && candle.Interval == interval
                && candle.OpenTime >= rangeFrom
                && candle.OpenTime <= to)
            .ToListAsync(cancellationToken);
    }

    private static List<DateTimeOffset> MissingSlots(
        IReadOnlyList<Candle> cached,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan step,
        CandleIntervalOptions intervalOptions,
        DateTimeOffset now)
    {
        var byOpen = cached.ToDictionary(candle => candle.OpenTime);
        var cacheLifetime = TimeSpan.FromSeconds(intervalOptions.OpenCacheSeconds);
        var missing = new List<DateTimeOffset>();
        for (var slot = FloorTo(from, step); slot <= to; slot = slot.Add(step))
        {
            if (!byOpen.TryGetValue(slot, out var row))
            {
                missing.Add(slot);
                continue;
            }

            var forming = slot + step > now;
            if (forming && now - row.FetchedAt > cacheLifetime)
            {
                missing.Add(slot);
            }
        }

        return missing;
    }

    private async Task StoreAsync(
        string instrumentId,
        string interval,
        TimeSpan step,
        IReadOnlyList<ProviderCandle> providerCandles,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (providerCandles.Count == 0)
        {
            return;
        }

        var lowest = providerCandles.Min(candle => candle.OpenTime);
        var highest = providerCandles.Max(candle => candle.OpenTime);
        var existing = await db.Candles
            .Where(candle => candle.SubjectType == MarketDataVocabulary.SubjectInstrument
                && candle.SubjectId == instrumentId
                && candle.Interval == interval
                && candle.OpenTime >= lowest
                && candle.OpenTime <= highest)
            .ToListAsync(cancellationToken);
        var byOpen = existing.ToDictionary(candle => candle.OpenTime);

        foreach (var candle in providerCandles)
        {
            if (candle.Open <= 0m || candle.High <= 0m || candle.Low <= 0m || candle.Close <= 0m
                || candle.High < candle.Low || candle.Volume < 0m)
            {
                logger.LogWarning(
                    "Rejected market data candle for instrument {InstrumentId} interval {Interval}.",
                    instrumentId,
                    interval);
                continue;
            }

            var closed = candle.OpenTime + step <= now;
            if (byOpen.TryGetValue(candle.OpenTime, out var row))
            {
                if (closed)
                {
                    continue;
                }

                row.Open = candle.Open;
                row.High = candle.High;
                row.Low = candle.Low;
                row.Close = candle.Close;
                row.Volume = candle.Volume;
                row.Adjusted = candle.Adjusted;
                row.Partial = true;
                row.ProviderKey = provider.ProviderKey;
                row.FetchedAt = now;
                continue;
            }

            db.Candles.Add(new Candle
            {
                SubjectType = MarketDataVocabulary.SubjectInstrument,
                SubjectId = instrumentId,
                Interval = interval,
                OpenTime = candle.OpenTime,
                Open = candle.Open,
                High = candle.High,
                Low = candle.Low,
                Close = candle.Close,
                Volume = candle.Volume,
                Adjusted = candle.Adjusted,
                Partial = !closed,
                ProviderKey = provider.ProviderKey,
                FetchedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ProviderCandlesResult> FetchCandlesAsync(
        string providerSymbol,
        string interval,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ProviderTimeoutSeconds));

        var result = await provider.GetCandlesAsync(providerSymbol, interval, from, to, timeout.Token);
        if (result.Outcome == ProviderOutcome.RateLimited && result.RetryAfter is { } retryAfter)
        {
            await Task.Delay(retryAfter, timeProvider, cancellationToken);
            result = await provider.GetCandlesAsync(providerSymbol, interval, from, to, timeout.Token);
        }

        return result;
    }

    private CandleResponse BuildResponse(
        IReadOnlyList<Candle> rows,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan step,
        DateTimeOffset now,
        bool stale)
    {
        var items = rows
            .Where(candle => candle.OpenTime <= to && candle.OpenTime + step > from)
            .OrderBy(candle => candle.OpenTime)
            .Select(candle => new CandleItem(
                candle.OpenTime,
                candle.Open,
                candle.High,
                candle.Low,
                candle.Close,
                candle.Volume,
                candle.Adjusted,
                false))
            .ToList();
        if (items.Count > 0)
        {
            var last = items[^1];
            items[^1] = last with { Partial = last.OpenTime + step > now };
        }

        return new CandleResponse(options.Value.ProviderDisplayName, stale, items);
    }

    private static Result<CandleResponse> Invalid(string field, string message)
        => Result.Failure<CandleResponse>(new Error(
            ErrorCodes.ValidationFailed,
            "The candle request is invalid.",
            ErrorType.Validation,
            [new ValidationFailure(field, message)]));

    private static long SlotCount(DateTimeOffset from, DateTimeOffset to, TimeSpan step)
    {
        var first = FloorTo(from, step);
        var last = FloorTo(to, step);
        return (last - first).Ticks / step.Ticks + 1;
    }

    private static TimeSpan StepFor(string interval) => interval switch
    {
        "15m" => Interval15m,
        "1h" => Interval1h,
        "1d" => Interval1d,
        _ => TimeSpan.Zero,
    };

    private static DateTimeOffset FloorTo(DateTimeOffset value, TimeSpan step)
    {
        var remainder = value.UtcTicks % step.Ticks;
        return new DateTimeOffset(value.UtcTicks - remainder, TimeSpan.Zero);
    }

    internal sealed record CandleResponse(string Source, bool Stale, IReadOnlyList<CandleItem> Candles);

    internal sealed record CandleItem(
        DateTimeOffset OpenTime,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume,
        bool Adjusted,
        bool Partial);
}

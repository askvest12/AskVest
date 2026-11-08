using AskVest.Modules.MarketData.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.MarketData.Providers;

internal sealed class FakeMarketDataProvider : IMarketDataProvider
{
    private static readonly TimeSpan Interval15m = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Interval1h = TimeSpan.FromHours(1);
    private static readonly TimeSpan Interval1d = TimeSpan.FromDays(1);

    private readonly FakeProviderOptions _options;
    private readonly TimeProvider _timeProvider;
    private string _scenario;

    public FakeMarketDataProvider(IOptions<MarketDataOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value.Fake;
        _timeProvider = timeProvider;
        _scenario = _options.Scenario;
    }

    public string ProviderKey => "fake";

    public MarketDataCapabilities Capabilities => new(
        DelayMinutes: _scenario == "delayed" ? _options.DelayMinutes : 0,
        MaxBatchSize: 200,
        SupportedIntervals: new HashSet<string>(StringComparer.Ordinal) { "15m", "1h", "1d" },
        ReportsSession: true);

    public string Scenario
    {
        get => _scenario;
        set => _scenario = value;
    }

    public void SetScenario(string scenario) => _scenario = scenario;

    internal int QuotesCallCount { get; private set; }

    internal int CandlesCallCount { get; private set; }

    public Task<ProviderQuotesResult> GetLatestQuotesAsync(
        IReadOnlyList<string> providerSymbols,
        CancellationToken cancellationToken)
    {
        QuotesCallCount++;
        var scenario = _scenario;
        switch (scenario)
        {
            case "outage":
                return Task.FromResult(Failure(ProviderOutcome.TransientFailure));
            case "rate_limited":
                return Task.FromResult(new ProviderQuotesResult(
                    ProviderOutcome.RateLimited,
                    TimeSpan.FromSeconds(_options.RateLimitRetrySeconds),
                    [],
                    []));
            case "unknown_symbol":
                {
                    var unknown = providerSymbols
                        .Where(symbol => symbol.Contains("UNKNOWN", StringComparison.Ordinal))
                        .ToList();
                    var known = providerSymbols.Except(unknown, StringComparer.Ordinal).ToList();
                    var nowUnknown = _timeProvider.GetUtcNow();
                    var sessionUnknown = SessionFor(scenario);
                    var partialQuotes = known
                        .Select(symbol => new ProviderQuote(
                            symbol,
                            PriceFor(symbol, scenario),
                            _options.Currency,
                            nowUnknown,
                            sessionUnknown,
                            null))
                        .ToList();
                    return Task.FromResult(new ProviderQuotesResult(
                        ProviderOutcome.Succeeded,
                        null,
                        partialQuotes,
                        unknown));
                }
        }

        var now = _timeProvider.GetUtcNow();
        var session = SessionFor(scenario);
        var quotes = providerSymbols
            .Select(symbol => new ProviderQuote(
                symbol,
                PriceFor(symbol, scenario),
                _options.Currency,
                now,
                session,
                scenario == "closed" ? BasePrice(symbol) : null))
            .ToList();
        return Task.FromResult(new ProviderQuotesResult(ProviderOutcome.Succeeded, null, quotes, []));
    }

    public Task<ProviderCandlesResult> GetCandlesAsync(
        string providerSymbol,
        string interval,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        CandlesCallCount++;
        var scenario = _scenario;
        switch (scenario)
        {
            case "outage":
                return Task.FromResult(FailureCandles(ProviderOutcome.TransientFailure));
            case "rate_limited":
                return Task.FromResult(new ProviderCandlesResult(
                    ProviderOutcome.RateLimited,
                    TimeSpan.FromSeconds(_options.RateLimitRetrySeconds),
                    []));
            case "unknown_symbol":
                return Task.FromResult(FailureCandles(ProviderOutcome.SymbolNotFound));
        }

        var step = StepFor(interval);
        var candles = new List<ProviderCandle>();
        for (var open = FloorTo(from, step); open <= to; open = open.Add(step))
        {
            candles.Add(CandleFor(providerSymbol, interval, open));
        }

        return Task.FromResult(new ProviderCandlesResult(ProviderOutcome.Succeeded, null, candles));
    }

    private string SessionFor(string scenario) => scenario switch
    {
        "delayed" => "regular",
        "halted" => "halted",
        "closed" => "closed",
        "outlier_spike" => "regular",
        "drop" => "regular",
        _ => _options.Session,
    };

    private decimal PriceFor(string symbol, string scenario)
    {
        if (scenario == "outlier_spike")
        {
            return BasePrice(symbol) * 6m;
        }

        if (scenario == "drop")
        {
            // Below the outlier tolerance so the drop is accepted as a real move.
            return BasePrice(symbol) * (100m - _options.DropPercent) / 100m;
        }

        var bucket = _timeProvider.GetUtcNow().UtcTicks / TimeSpan.TicksPerMinute / 5;
        var jitter = StableHash($"{symbol}|{bucket}") % 21;
        return BasePrice(symbol) + jitter / 100m;
    }

    private static ProviderCandle CandleFor(string symbol, string interval, DateTimeOffset openTime)
    {
        var key = $"{symbol}|{interval}|{openTime.UtcTicks}";
        var open = BasePrice(symbol) + (StableHash($"{key}|o") % 30) / 100m;
        var close = BasePrice(symbol) + (StableHash($"{key}|c") % 30) / 100m;
        var high = Math.Max(open, close) + (StableHash($"{key}|h") % 20) / 100m;
        var low = Math.Max(0.01m, Math.Min(open, close) - (StableHash($"{key}|l") % 20) / 100m);
        var volume = StableHash($"{key}|v") % 100000;
        return new ProviderCandle(openTime, Round4(open), Round4(high), Round4(low), Round4(close), volume, true);
    }

    private static decimal Round4(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static decimal BasePrice(string symbol) => 20m + (StableHash(symbol) % 800) / 10m;

    private static ulong StableHash(string text)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= prime;
        }

        return hash;
    }

    private static TimeSpan StepFor(string interval) => interval switch
    {
        "15m" => Interval15m,
        "1h" => Interval1h,
        "1d" => Interval1d,
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unsupported candle interval."),
    };

    private static DateTimeOffset FloorTo(DateTimeOffset value, TimeSpan step)
    {
        var remainder = value.UtcTicks % step.Ticks;
        return new DateTimeOffset(value.UtcTicks - remainder, TimeSpan.Zero);
    }

    private static ProviderQuotesResult Failure(ProviderOutcome outcome) =>
        new(outcome, null, [], []);

    private static ProviderCandlesResult FailureCandles(ProviderOutcome outcome) =>
        new(outcome, null, []);
}

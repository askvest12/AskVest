using System.Collections.ObjectModel;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.MarketData.Configuration;

public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    private static readonly HashSet<string> SupportedIntervals = Domain.MarketDataVocabulary.Intervals;
    private static readonly HashSet<string> SupportedSessions = Domain.MarketDataVocabulary.Sessions;
    private static readonly HashSet<string> SupportedScenarios = Domain.MarketDataVocabulary.Scenarios;

    public string Provider { get; set; } = "fake";

    public string ProviderDisplayName { get; set; } = "AskVest Demo Feed";

    public int PollIntervalSeconds { get; set; } = 60;

    public int BatchSize { get; set; } = 50;

    public int LiveThresholdSeconds { get; set; } = 30;

    public int StaleThresholdSeconds { get; set; } = 300;

    public decimal MaxTickChangePercent { get; set; } = 30m;

    public int MaxFractionalDigits { get; set; } = 8;

    public int ProviderTimeoutSeconds { get; set; } = 5;

    public int RequestsPerMinute { get; set; } = 240;

    public Dictionary<string, string> SymbolOverrides { get; } = new(StringComparer.Ordinal);

    public Collection<CandleIntervalOptions> CandleIntervals { get; } = [];

    public FakeProviderOptions Fake { get; set; } = new();

    internal void ApplyIntervalDefaults()
    {
        if (CandleIntervals.Count > 0)
        {
            return;
        }

        CandleIntervals.Add(new CandleIntervalOptions { Interval = "15m", MaxRangeDays = 7, MaxCandles = 500, OpenCacheSeconds = 120 });
        CandleIntervals.Add(new CandleIntervalOptions { Interval = "1h", MaxRangeDays = 60, MaxCandles = 500, OpenCacheSeconds = 300 });
        CandleIntervals.Add(new CandleIntervalOptions { Interval = "1d", MaxRangeDays = 1826, MaxCandles = 500, OpenCacheSeconds = 3600 });
    }

    public static IValidateOptions<MarketDataOptions> Validator { get; } = new MarketDataOptionsValidator();

    private sealed class MarketDataOptionsValidator : IValidateOptions<MarketDataOptions>
    {
        public ValidateOptionsResult Validate(string? name, MarketDataOptions options)
        {
            var failures = new List<string>();
            void Check(bool condition, string message)
            {
                if (!condition)
                {
                    failures.Add(message);
                }
            }

            Check(!string.IsNullOrWhiteSpace(options.Provider), "MarketData:Provider is required.");
            Check(
                Providers.ProviderRegistry.IsSupported(options.Provider),
                $"MarketData:Provider '{options.Provider}' is not supported.");
            Check(!string.IsNullOrWhiteSpace(options.ProviderDisplayName), "MarketData:ProviderDisplayName is required.");
            Check(options.PollIntervalSeconds > 0, "MarketData:PollIntervalSeconds must be positive.");
            Check(options.BatchSize is >= 1 and <= 500, "MarketData:BatchSize must be between 1 and 500.");
            Check(options.LiveThresholdSeconds > 0, "MarketData:LiveThresholdSeconds must be positive.");
            Check(options.StaleThresholdSeconds > options.LiveThresholdSeconds,
                "MarketData:StaleThresholdSeconds must exceed LiveThresholdSeconds.");
            Check(options.MaxTickChangePercent > 0m, "MarketData:MaxTickChangePercent must be positive.");
            Check(options.MaxFractionalDigits is >= 0 and <= 18, "MarketData:MaxFractionalDigits must be between 0 and 18.");
            Check(options.ProviderTimeoutSeconds is >= 1 and <= 60, "MarketData:ProviderTimeoutSeconds must be between 1 and 60.");
            Check(options.RequestsPerMinute > 0, "MarketData:RequestsPerMinute must be positive.");
            Check(options.SymbolOverrides.Values.All(v => !string.IsNullOrWhiteSpace(v)),
                "MarketData:SymbolOverrides values must not be empty.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            Check(options.CandleIntervals.Count > 0, "MarketData:CandleIntervals needs at least one interval.");
            foreach (var interval in options.CandleIntervals)
            {
                Check(SupportedIntervals.Contains(interval.Interval),
                    $"MarketData candle interval '{interval.Interval}' is not supported.");
                Check(seen.Add(interval.Interval), $"MarketData candle interval '{interval.Interval}' is repeated.");
                Check(interval.MaxRangeDays > 0, $"MarketData candle interval '{interval.Interval}' needs a positive MaxRangeDays.");
                Check(interval.MaxCandles is >= 1 and <= 500,
                    $"MarketData candle interval '{interval.Interval}' MaxCandles must be between 1 and 500.");
                Check(interval.OpenCacheSeconds > 0,
                    $"MarketData candle interval '{interval.Interval}' needs a positive OpenCacheSeconds.");
            }

            Check(SupportedScenarios.Contains(options.Fake.Scenario),
                $"MarketData:Fake:Scenario '{options.Fake.Scenario}' is not supported.");
            Check(SupportedSessions.Contains(options.Fake.Session),
                $"MarketData:Fake:Session '{options.Fake.Session}' is not supported.");
            Check(options.Fake.DelayMinutes >= 0, "MarketData:Fake:DelayMinutes must not be negative.");
            Check(options.Fake.RateLimitRetrySeconds > 0, "MarketData:Fake:RateLimitRetrySeconds must be positive.");
            Check(!string.IsNullOrWhiteSpace(options.Fake.Currency), "MarketData:Fake:Currency is required.");

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }
    }
}

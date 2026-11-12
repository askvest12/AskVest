using Microsoft.Extensions.Options;

namespace AskVest.Modules.Trading.Configuration;

/// <summary>
/// Trading configuration, validated at startup. Limits are conservative
/// defaults; the settlement asset comes from the gateway route, never here.
/// </summary>
public sealed class TradingOptions
{
    public const string SectionName = "Trading";

    public bool BuyEnabled { get; set; } = true;

    public bool SellEnabled { get; set; } = true;

    public int QuoteLifetimeSeconds { get; set; } = 30;

    public decimal MaxQuoteDeviationPercent { get; set; } = 3m;

    public int FeeBasisPoints { get; set; }

    public decimal MinOrderAmount { get; set; } = 5m;

    public decimal MaxOrderAmount { get; set; } = 1000m;

    public decimal Rolling24HourMax { get; set; } = 2000m;

    public int MaxNonFinalOrders { get; set; } = 5;

    public int QuotesPerUserPerMinute { get; set; } = 30;

    /// <summary>
    /// How often the progress job polls the gateway. Submission dispatch itself
    /// rides the shared outbox interval (Ledger:Outbox:DispatchIntervalSeconds).
    /// </summary>
    public int ProgressIntervalSeconds { get; set; } = 5;

    public int SubmissionStuckSeconds { get; set; } = 60;

    public int SubmittedTimeoutSeconds { get; set; } = 900;

    public int GatewayTimeoutSeconds { get; set; } = 10;

    public GatewayOptions Gateway { get; set; } = new();

    public FakeGatewayOptions Fake { get; set; } = new();

    public static IValidateOptions<TradingOptions> Validator { get; } = new TradingOptionsValidator();

    private sealed class TradingOptionsValidator : IValidateOptions<TradingOptions>
    {
        public ValidateOptionsResult Validate(string? name, TradingOptions options)
        {
            var failures = new List<string>();
            void Check(bool condition, string message)
            {
                if (!condition)
                {
                    failures.Add(message);
                }
            }

            Check(options.QuoteLifetimeSeconds is >= 5 and <= 300,
                "Trading:QuoteLifetimeSeconds must be between 5 and 300.");
            Check(options.MaxQuoteDeviationPercent is >= 0m and <= 100m,
                "Trading:MaxQuoteDeviationPercent must be between 0 and 100.");
            Check(options.FeeBasisPoints is >= 0 and <= 10000,
                "Trading:FeeBasisPoints must be between 0 and 10000.");
            Check(options.MinOrderAmount > 0m, "Trading:MinOrderAmount must be positive.");
            Check(options.MaxOrderAmount >= options.MinOrderAmount,
                "Trading:MaxOrderAmount must be at least Trading:MinOrderAmount.");
            Check(options.Rolling24HourMax >= options.MaxOrderAmount,
                "Trading:Rolling24HourMax must be at least Trading:MaxOrderAmount.");
            Check(options.MaxNonFinalOrders > 0, "Trading:MaxNonFinalOrders must be positive.");
            Check(options.QuotesPerUserPerMinute > 0, "Trading:QuotesPerUserPerMinute must be positive.");
            Check(options.ProgressIntervalSeconds is >= 1 and <= 300,
                "Trading:ProgressIntervalSeconds must be between 1 and 300.");
            Check(options.SubmissionStuckSeconds >= 10,
                "Trading:SubmissionStuckSeconds must be at least 10.");
            Check(options.SubmittedTimeoutSeconds >= 60,
                "Trading:SubmittedTimeoutSeconds must be at least 60.");
            Check(options.GatewayTimeoutSeconds is >= 1 and <= 120,
                "Trading:GatewayTimeoutSeconds must be between 1 and 120.");
            foreach (var (key, route) in options.Gateway.Routes)
            {
                Check(!string.IsNullOrWhiteSpace(key), "Trading:Gateway:Routes keys must not be empty.");
                Check(route.Provider is "fake",
                    $"Trading:Gateway:Routes:{key}:Provider '{route.Provider}' has no adapter; only 'fake' is implemented.");
                Check(!string.IsNullOrWhiteSpace(route.SettlementAsset),
                    $"Trading:Gateway:Routes:{key}:SettlementAsset is required.");
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }
    }
}

using System.Collections.ObjectModel;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Funding.Configuration;

/// <summary>
/// Funding configuration. Every rail, currency, pair, fee and limit is
/// data here; the module contains no vendor, country or chain specific
/// code. Invalid values stop startup.
/// </summary>
public sealed class FundingOptions
{
    public const string SectionName = "Funding";

    /// <summary>One funding method per local currency.</summary>
    public Collection<FundingCurrencyOption> Currencies { get; } = [];

    /// <summary>Internal conversion pairs between cash assets.</summary>
    public Collection<FundingPairOption> Pairs { get; } = [];

    /// <summary>Payment providers by route key, with webhook secrets.</summary>
    public Collection<FundingProviderOption> Providers { get; } = [];

    /// <summary>The single FX rate provider route.</summary>
    public string FxProvider { get; set; } = string.Empty;

    /// <summary>
    /// Mid rates per "FROM:TO" pair used by the fake FX provider. Required
    /// for every enabled pair while the fake is configured.
    /// </summary>
    public Dictionary<string, decimal> FxRates { get; } = new(StringComparer.Ordinal);

    public int ProviderTimeoutSeconds { get; set; } = 10;

    public int WebhookToleranceSeconds { get; set; } = 300;

    public int MaxWebhookBodyBytes { get; set; } = 65_536;

    public int ConversionQuoteLifetimeSeconds { get; set; } = 30;

    public int WithdrawalPreviewLifetimeSeconds { get; set; } = 60;

    public int DepositPollIntervalSeconds { get; set; } = 30;

    public int PayoutPollIntervalSeconds { get; set; } = 15;

    public int CleanupIntervalSeconds { get; set; } = 300;

    public int SubmissionStuckSeconds { get; set; } = 120;

    public int SubmittedTimeoutSeconds { get; set; } = 900;

    public int ReservesIntervalMinutes { get; set; } = 10;

    /// <summary>Consecutive reserve shortfalls before withdrawals pause.</summary>
    public int ReservesShortfallThreshold { get; set; } = 2;

    /// <summary>
    /// Cash deposits credited inside this window are not withdrawable.
    /// Fractional values allowed for smoke configurations.
    /// </summary>
    public decimal FreshDepositLockHours { get; set; } = 72m;

    public decimal BeneficiaryCoolingOffHours { get; set; } = 24m;

    public int BeneficiaryMaximum { get; set; } = 5;

    /// <summary>Accounts younger than this are reviewed on every withdrawal.</summary>
    public decimal ReviewMinAccountAgeHours { get; set; } = 168m;

    /// <summary>FX rates older than this are refused.</summary>
    public int FxMaxAgeSeconds { get; set; } = 60;

    /// <summary>Operator-declared withdrawal pauses per currency.</summary>
    public Collection<FundingPauseOption> ManualPauses { get; } = [];

    public static IValidateOptions<FundingOptions> Validator { get; } = new FundingOptionsValidator();

    private sealed class FundingOptionsValidator : IValidateOptions<FundingOptions>
    {
        public ValidateOptionsResult Validate(string? name, FundingOptions options)
        {
            // An absent Funding section leaves the module inert; once any
            // rail is configured every rule below applies.
            if (options.Providers.Count == 0
                && options.Currencies.Count == 0
                && options.Pairs.Count == 0
                && options.FxProvider.Length == 0
                && options.ManualPauses.Count == 0)
            {
                return ValidateOptionsResult.Success;
            }

            var failures = new List<string>();
            void Check(bool condition, string message)
            {
                if (!condition)
                {
                    failures.Add(message);
                }
            }

            Check(options.ProviderTimeoutSeconds is >= 1 and <= 120,
                "Funding:ProviderTimeoutSeconds must be between 1 and 120.");
            Check(options.WebhookToleranceSeconds is >= 30 and <= 900,
                "Funding:WebhookToleranceSeconds must be between 30 and 900.");
            Check(options.MaxWebhookBodyBytes is >= 1024 and <= 1_048_576,
                "Funding:MaxWebhookBodyBytes must be between 1024 and 1048576.");
            Check(options.ConversionQuoteLifetimeSeconds is >= 5 and <= 300,
                "Funding:ConversionQuoteLifetimeSeconds must be between 5 and 300.");
            Check(options.WithdrawalPreviewLifetimeSeconds is >= 5 and <= 600,
                "Funding:WithdrawalPreviewLifetimeSeconds must be between 5 and 600.");
            Check(options.DepositPollIntervalSeconds is >= 1 and <= 300,
                "Funding:DepositPollIntervalSeconds must be between 1 and 300.");
            Check(options.PayoutPollIntervalSeconds is >= 1 and <= 300,
                "Funding:PayoutPollIntervalSeconds must be between 1 and 300.");
            Check(options.CleanupIntervalSeconds is >= 10 and <= 3600,
                "Funding:CleanupIntervalSeconds must be between 10 and 3600.");
            Check(options.SubmissionStuckSeconds >= 10,
                "Funding:SubmissionStuckSeconds must be at least 10.");
            Check(options.SubmittedTimeoutSeconds >= 60,
                "Funding:SubmittedTimeoutSeconds must be at least 60.");
            Check(options.ReservesIntervalMinutes is >= 1 and <= 1440,
                "Funding:ReservesIntervalMinutes must be between 1 and 1440.");
            Check(options.ReservesShortfallThreshold >= 2,
                "Funding:ReservesShortfallThreshold must be at least 2.");
            Check(options.FreshDepositLockHours is >= 0m and <= 720m,
                "Funding:FreshDepositLockHours must be between 0 and 720.");
            Check(options.BeneficiaryCoolingOffHours is >= 0m and <= 720m,
                "Funding:BeneficiaryCoolingOffHours must be between 0 and 720.");
            Check(options.BeneficiaryMaximum is >= 1 and <= 20,
                "Funding:BeneficiaryMaximum must be between 1 and 20.");
            Check(options.ReviewMinAccountAgeHours is >= 0m and <= 8760m,
                "Funding:ReviewMinAccountAgeHours must be between 0 and 8760.");
            Check(options.FxMaxAgeSeconds is >= 5 and <= 600,
                "Funding:FxMaxAgeSeconds must be between 5 and 600.");

            var providers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var provider in options.Providers)
            {
                Check(!string.IsNullOrWhiteSpace(provider.Key),
                    "Funding:Providers entries require a Key.");
                Check(provider.WebhookSecret is { Length: >= 32 },
                    $"Funding:Providers:{provider.Key} webhook secret must be at least 32 bytes.");
                Check(provider.Adapter is "fake",
                    $"Funding:Providers:{provider.Key} adapter '{provider.Adapter}' has no implementation; only 'fake' exists.");
                Check(providers.Add(provider.Key),
                    $"Funding:Providers has a duplicate key '{provider.Key}'.");
            }

            var currencies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var currency in options.Currencies)
            {
                Check(IsCurrencyCode(currency.Code),
                    $"Funding:Currencies code '{currency.Code}' must be 3 uppercase letters.");
                Check(currency.Scale is >= 0 and <= 8,
                    $"Funding:Currencies:{currency.Code} scale must be between 0 and 8.");
                Check(currency.Countries.Count > 0,
                    $"Funding:Currencies:{currency.Code} requires at least one country.");
                Check(currency.Provider.Length > 0 && providers.Contains(currency.Provider),
                    $"Funding:Currencies:{currency.Code} provider '{currency.Provider}' is not a configured provider.");
                Check(currency.DepositMinimum >= 0m && currency.DepositMaximum >= currency.DepositMinimum,
                    $"Funding:Currencies:{currency.Code} deposit limits are invalid.");
                Check(currency.DepositDailyMaximum >= currency.DepositMaximum,
                    $"Funding:Currencies:{currency.Code} daily deposit cap must be at least the per-deposit maximum.");
                Check(currency.WithdrawalMinimum > 0m && currency.WithdrawalMaximum >= currency.WithdrawalMinimum,
                    $"Funding:Currencies:{currency.Code} withdrawal limits are invalid.");
                Check(currency.WithdrawalDaily24Maximum >= currency.WithdrawalMaximum
                    && currency.WithdrawalDaily30Maximum >= currency.WithdrawalDaily24Maximum,
                    $"Funding:Currencies:{currency.Code} rolling withdrawal caps are invalid.");
                Check(currency.WithdrawalFlatFee >= 0m && currency.WithdrawalFeeBps is >= 0 and <= 10_000,
                    $"Funding:Currencies:{currency.Code} withdrawal fee is invalid.");
                Check(currency.ReviewThresholdValue >= 0m,
                    $"Funding:Currencies:{currency.Code} review threshold must not be negative.");
                Check(currencies.Add(currency.Code),
                    $"Funding:Currencies has a duplicate code '{currency.Code}'.");
            }

            var pairs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in options.Pairs)
            {
                // Endpoints are ledger asset codes: local currencies from
                // Funding:Currencies plus seeded cash assets like USD and
                // USDC, so they are not limited to three letters.
                Check(IsAssetCode(pair.From) && IsAssetCode(pair.To)
                    && !string.Equals(pair.From, pair.To, StringComparison.Ordinal),
                    $"Funding:Pairs entry '{pair.From}:{pair.To}' is invalid.");
                Check(pair.SpreadBps is >= 0 and <= 10_000 && pair.FeeBps is >= 0 and <= 10_000,
                    $"Funding:Pairs:{pair.From}:{pair.To} spread or fee is out of range.");
                Check(pair.MinimumSource > 0m && pair.MaximumSource >= pair.MinimumSource
                    && pair.DailyCapSource >= pair.MaximumSource,
                    $"Funding:Pairs:{pair.From}:{pair.To} limits are invalid.");
                Check(pair.MaxJumpPercent is > 0m and <= 100m,
                    $"Funding:Pairs:{pair.From}:{pair.To} jump limit must be between 0 and 100 percent.");
                Check(pairs.Add($"{pair.From}:{pair.To}"),
                    $"Funding:Pairs has a duplicate '{pair.From}:{pair.To}'.");
            }

            foreach (var pause in options.ManualPauses)
            {
                Check(currencies.Contains(pause.Currency),
                    $"Funding:ManualPauses currency '{pause.Currency}' is not a configured currency.");
                Check(!string.IsNullOrWhiteSpace(pause.Reason),
                    "Funding:ManualPauses entries require a Reason.");
            }

            Check(options.FxProvider is "fake",
                $"Funding:FxProvider '{options.FxProvider}' has no adapter; only 'fake' is implemented.");
            if (options.FxProvider == "fake")
            {
                foreach (var pair in options.Pairs.Where(pair => pair.Enabled))
                {
                    var key = $"{pair.From}:{pair.To}";
                    Check(options.FxRates.TryGetValue(key, out var rate) && rate > 0m,
                        $"Funding:FxRates is missing a positive rate for enabled pair '{key}'.");
                }
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        private static bool IsCurrencyCode(string code)
            => code.Length == 3 && code.All(c => c is >= 'A' and <= 'Z');

        private static bool IsAssetCode(string code)
            => code.Trim().Length is >= 1 and <= 16
                && code.Trim().All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');
    }
}

/// <summary>One local currency funding method with its limits and fees.</summary>
public sealed class FundingCurrencyOption
{
    public string Code { get; set; } = string.Empty;

    /// <summary>Ledger scale for amounts in this currency.</summary>
    public int Scale { get; set; } = 2;

    /// <summary>ISO country codes this method serves.</summary>
    public Collection<string> Countries { get; } = [];

    /// <summary>Provider route key from Funding:Providers.</summary>
    public string Provider { get; set; } = string.Empty;

    public decimal DepositMinimum { get; set; }

    public decimal DepositMaximum { get; set; }

    public decimal DepositDailyMaximum { get; set; }

    public decimal WithdrawalMinimum { get; set; }

    public decimal WithdrawalMaximum { get; set; }

    public decimal WithdrawalDaily24Maximum { get; set; }

    public decimal WithdrawalDaily30Maximum { get; set; }

    /// <summary>Flat cash withdrawal fee in currency units.</summary>
    public decimal WithdrawalFlatFee { get; set; }

    /// <summary>Cash withdrawal fee in basis points of the receive amount.</summary>
    public int WithdrawalFeeBps { get; set; }

    /// <summary>Withdrawals at or above this value go to review.</summary>
    public decimal ReviewThresholdValue { get; set; }
}

/// <summary>One internal conversion pair between two cash assets.</summary>
public sealed class FundingPairOption
{
    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    /// <summary>Spread applied to the mid rate, in basis points.</summary>
    public int SpreadBps { get; set; }

    /// <summary>Fee on the receive amount, in basis points.</summary>
    public int FeeBps { get; set; }

    public decimal MinimumSource { get; set; }

    public decimal MaximumSource { get; set; }

    /// <summary>Rolling 24 hour cap on source amount.</summary>
    public decimal DailyCapSource { get; set; }

    /// <summary>Maximum allowed deviation from the last accepted rate, percent.</summary>
    public decimal MaxJumpPercent { get; set; } = 5m;

    public bool Enabled { get; set; } = true;
}

/// <summary>One payment provider route with its webhook secret.</summary>
public sealed class FundingProviderOption
{
    public string Key { get; set; } = string.Empty;

    /// <summary>Adapter key; only "fake" exists until real adapters land.</summary>
    public string Adapter { get; set; } = string.Empty;

    /// <summary>HMAC secret for webhook signature verification.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Settlement balances per currency reported by the fake provider.
    /// Real adapters read these from the provider instead.
    /// </summary>
    public Dictionary<string, decimal> SettlementBalances { get; } = new(StringComparer.Ordinal);
}

/// <summary>An operator-declared withdrawal pause for one currency.</summary>
public sealed class FundingPauseOption
{
    public string Currency { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}

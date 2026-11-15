using System.Collections.ObjectModel;
using AskVest.Modules.Wallets.Custody;
using AskVest.Modules.Wallets.Screening;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets.Configuration;

/// <summary>
/// Wallets configuration, validated at startup. Limits are conservative
/// defaults; every value is plain configuration, never a secret.
/// </summary>
public sealed class WalletsOptions
{
    public const string SectionName = "Wallets";

    /// <summary>
    /// Cooling-off period in hours; fractional values give sub-hour
    /// precision for smoke and test configurations.
    /// </summary>
    public decimal AddressCoolingOffHours { get; set; } = 24m;

    public int AddressBookMaximum { get; set; } = 10;

    public int PreviewLifetimeSeconds { get; set; } = 60;

    public decimal WithdrawalMinQuantity { get; set; } = 0.000001m;

    public decimal MaxWithdrawalValueUsd { get; set; } = 500m;

    public decimal Rolling24HourMaxValueUsd { get; set; } = 2000m;

    /// <summary>
    /// Flat withdrawal fee per token in token units, keyed by token id in
    /// "D" format. Tokens absent from the map pay no fee.
    /// </summary>
    public Dictionary<string, decimal> WithdrawalFees { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Minimum deposit quantity per token in token units, keyed by token id
    /// in "D" format. Tokens absent from the map use the default below.
    /// </summary>
    public Dictionary<string, decimal> DustMinimums { get; } = new(StringComparer.Ordinal);

    public decimal DefaultDustMinimum { get; set; } = 0.000001m;

    public int DepositIntervalSeconds { get; set; } = 10;

    public int ProgressIntervalSeconds { get; set; } = 10;

    public int SubmissionStuckSeconds { get; set; } = 60;

    public int SubmittedTimeoutSeconds { get; set; } = 900;

    public int ProviderTimeoutSeconds { get; set; } = 10;

    public int ReservesIntervalMinutes { get; set; } = 10;

    public int ReservesMismatchThreshold { get; set; } = 2;

    /// <summary>
    /// Operator-declared pauses by chain and optional token id. A pause
    /// blocks previews and placements for the scope until an operator
    /// resumes it (Phase 15 tooling; until then a documented database
    /// operation).
    /// </summary>
    public Collection<CustodyPauseOption> ManualPauses { get; } = [];

    /// <summary>
    /// Stablecoin contracts whose deposits credit a cash ledger asset
    /// instead of a catalog token. Each entry names the chain, the canonical
    /// contract, the cash asset code and its decimals; the startup validator
    /// proves every entry through the Catalog address rules.
    /// </summary>
    public Collection<ApprovedStablecoinOption> ApprovedStablecoins { get; } = [];

    public CustodyOptions Custody { get; set; } = new();

    public ScreeningOptions Screening { get; set; } = new();

    public static IValidateOptions<WalletsOptions> Validator { get; } = new WalletsOptionsValidator();

    private sealed class WalletsOptionsValidator : IValidateOptions<WalletsOptions>
    {
        public ValidateOptionsResult Validate(string? name, WalletsOptions options)
        {
            var failures = new List<string>();
            void Check(bool condition, string message)
            {
                if (!condition)
                {
                    failures.Add(message);
                }
            }

            Check(options.AddressCoolingOffHours is >= 0m and <= 720m,
                "Wallets:AddressCoolingOffHours must be between 0 and 720.");
            Check(options.AddressBookMaximum is >= 1 and <= 100,
                "Wallets:AddressBookMaximum must be between 1 and 100.");
            Check(options.PreviewLifetimeSeconds is >= 5 and <= 600,
                "Wallets:PreviewLifetimeSeconds must be between 5 and 600.");
            Check(options.WithdrawalMinQuantity > 0m,
                "Wallets:WithdrawalMinQuantity must be positive.");
            Check(options.MaxWithdrawalValueUsd > 0m,
                "Wallets:MaxWithdrawalValueUsd must be positive.");
            Check(options.Rolling24HourMaxValueUsd >= options.MaxWithdrawalValueUsd,
                "Wallets:Rolling24HourMaxValueUsd must be at least Wallets:MaxWithdrawalValueUsd.");
            Check(options.DefaultDustMinimum >= 0m,
                "Wallets:DefaultDustMinimum must not be negative.");
            foreach (var (key, fee) in options.WithdrawalFees)
            {
                Check(Guid.TryParse(key, out _), $"Wallets:WithdrawalFees key '{key}' is not a token id.");
                Check(fee >= 0m, $"Wallets:WithdrawalFees:{key} must not be negative.");
            }

            foreach (var (key, dust) in options.DustMinimums)
            {
                Check(Guid.TryParse(key, out _), $"Wallets:DustMinimums key '{key}' is not a token id.");
                Check(dust >= 0m, $"Wallets:DustMinimums:{key} must not be negative.");
            }

            Check(options.DepositIntervalSeconds is >= 1 and <= 300,
                "Wallets:DepositIntervalSeconds must be between 1 and 300.");
            Check(options.ProgressIntervalSeconds is >= 1 and <= 300,
                "Wallets:ProgressIntervalSeconds must be between 1 and 300.");
            Check(options.SubmissionStuckSeconds >= 10,
                "Wallets:SubmissionStuckSeconds must be at least 10.");
            Check(options.SubmittedTimeoutSeconds >= 60,
                "Wallets:SubmittedTimeoutSeconds must be at least 60.");
            Check(options.ProviderTimeoutSeconds is >= 1 and <= 120,
                "Wallets:ProviderTimeoutSeconds must be between 1 and 120.");
            Check(options.ReservesIntervalMinutes is >= 1 and <= 1440,
                "Wallets:ReservesIntervalMinutes must be between 1 and 1440.");
            Check(options.ReservesMismatchThreshold >= 2,
                "Wallets:ReservesMismatchThreshold must be at least 2.");
            foreach (var pause in options.ManualPauses)
            {
                Check(!string.IsNullOrWhiteSpace(pause.ChainId),
                    "Wallets:ManualPauses entries require a ChainId.");
                Check(pause.TokenId is null || Guid.TryParse(pause.TokenId, out _),
                    $"Wallets:ManualPauses token id '{pause.TokenId}' is not a token id.");
            }

            var seenChains = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in options.Custody.Routes)
            {
                Check(!string.IsNullOrWhiteSpace(route.ChainId),
                    "Wallets:Custody:Routes entries require a ChainId.");
                Check(route.Provider is "fake",
                    $"Wallets:Custody:Routes provider '{route.Provider}' has no adapter; only 'fake' is implemented.");
                Check(seenChains.Add(route.ChainId),
                    $"Wallets:Custody:Routes has a duplicate chain id '{route.ChainId}'.");
            }

            Check(options.Screening.Provider is "fake",
                $"Wallets:Screening:Provider '{options.Screening.Provider}' has no adapter; only 'fake' is implemented.");

            var seenStablecoins = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stablecoin in options.ApprovedStablecoins)
            {
                Check(!string.IsNullOrWhiteSpace(stablecoin.ChainId),
                    "Wallets:ApprovedStablecoins entries require a ChainId.");
                Check(!string.IsNullOrWhiteSpace(stablecoin.Contract)
                    && stablecoin.Contract.Length <= 128,
                    "Wallets:ApprovedStablecoins entries require a Contract of at most 128 characters.");
                Check(stablecoin.LedgerAssetCode.Length is >= 1 and <= 16,
                    $"Wallets:ApprovedStablecoins asset code '{stablecoin.LedgerAssetCode}' must be 1 to 16 characters.");
                Check(stablecoin.Decimals is >= 0 and <= 18,
                    $"Wallets:ApprovedStablecoins decimals for '{stablecoin.LedgerAssetCode}' must be between 0 and 18.");
                Check(stablecoin.DustMinimum is null or >= 0m,
                    $"Wallets:ApprovedStablecoins dust minimum for '{stablecoin.LedgerAssetCode}' must not be negative.");
                Check(seenStablecoins.Add($"{stablecoin.ChainId}:{stablecoin.Contract}"),
                    $"Wallets:ApprovedStablecoins has a duplicate entry for chain '{stablecoin.ChainId}'.");
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }
    }
}

/// <summary>
/// One operator-declared custody pause scope. TokenId null pauses the whole
/// chain; a token id pauses only that token on the chain.
/// </summary>
public sealed class CustodyPauseOption
{
    public string ChainId { get; set; } = string.Empty;

    public string? TokenId { get; set; }
}

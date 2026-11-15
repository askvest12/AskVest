using AskVest.Modules.Funding.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Funding.Providers;

/// <summary>
/// Deterministic FX provider for development and tests. Mid rates come from
/// Funding:FxRates; scenarios stale, jump and error exercise the quote
/// safety rules.
/// </summary>
internal sealed class FakeFxRateProvider : IFxRateProvider
{
    public const string ScenarioNormal = "normal";
    public const string ScenarioStale = "stale";
    public const string ScenarioJump = "jump";
    public const string ScenarioError = "error";

    public static readonly string[] Scenarios =
        [ScenarioNormal, ScenarioStale, ScenarioJump, ScenarioError];

    private readonly IOptions<FundingOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, decimal> _lastRates = new(StringComparer.Ordinal);

    public FakeFxRateProvider(IOptions<FundingOptions> options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public string Scenario { get; set; } = ScenarioNormal;

    /// <summary>Test hook: clears jump detection between cases.</summary>
    public void Reset() => _lastRates.Clear();

    public Task<FundingFxRate> GetMidRateAsync(string pair, CancellationToken cancellationToken = default)
    {
        if (Scenario == ScenarioError)
        {
            throw new FundingProviderException(FundingProviderFailureKind.Transient, "fx_error");
        }

        var separator = pair.IndexOf(':', StringComparison.Ordinal);
        var entry = separator > 0
            ? _options.Value.FxRates.FirstOrDefault(rate =>
                string.Equals(rate.From, pair[..separator], StringComparison.Ordinal)
                && string.Equals(rate.To, pair[(separator + 1)..], StringComparison.Ordinal))
            : null;
        if (entry is null)
        {
            throw new FundingProviderException(FundingProviderFailureKind.Rejected, "fx_pair_unknown");
        }

        var baseRate = entry.Rate;

        var now = _timeProvider.GetUtcNow();
        var asOf = Scenario == ScenarioStale
            ? now - TimeSpan.FromSeconds(_options.Value.FxMaxAgeSeconds * 2)
            : now;

        var rate = baseRate;
        if (Scenario == ScenarioJump)
        {
            rate = _lastRates.TryGetValue(pair, out var last)
                ? last * 1.5m
                : baseRate * 1.5m;
        }

        _lastRates[pair] = rate;
        return Task.FromResult(new FundingFxRate(pair, rate, asOf, "fake"));
    }
}

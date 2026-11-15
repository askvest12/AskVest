namespace AskVest.Modules.Funding.Providers;

/// <summary>
/// One injected provider deposit waiting to be listed or web-hooked.
/// State is mutable so tests can advance pending to completed.
/// </summary>
internal sealed class FakeFundingDeposit
{
    public required string ProviderKey { get; init; }

    public required string ProviderDepositId { get; init; }

    public required string AccountReference { get; init; }

    public required string Currency { get; init; }

    public required decimal Amount { get; init; }

    public decimal ProviderFee { get; set; }

    public bool PayerVerified { get; set; } = true;

    public FundingDepositReportState State { get; set; } = FundingDepositReportState.Pending;

    public DateTimeOffset ObservedAt { get; set; }
}

/// <summary>
/// A payout the fake provider knows about, keyed by our withdrawal id.
/// </summary>
internal sealed class FakeFundingPayout
{
    public required string WithdrawalKey { get; init; }

    public required string BeneficiaryReference { get; init; }

    public required string Currency { get; init; }

    public required decimal Amount { get; init; }

    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>Whether the provider actually accepted the payout.</summary>
    public bool Filled { get; set; } = true;

    public FundingPayoutReportState State { get; set; } = FundingPayoutReportState.Pending;

    public string? DetailCode { get; set; }
}

/// <summary>
/// Deterministic in-memory state shared by every fake funding provider
/// instance. Cleared between test runs by constructing a new registry.
/// </summary>
internal sealed class FakeFundingState
{
    public const string ScenarioNormal = "normal";
    public const string ScenarioDelayed = "delayed";
    public const string ScenarioRejected = "rejected";
    public const string ScenarioSubmitTimeoutFilled = "submit_timeout_filled";
    public const string ScenarioSubmitTimeoutLost = "submit_timeout_lost";
    public const string ScenarioStatusUnavailable = "status_unavailable";
    public const string ScenarioNameMismatch = "name_mismatch";
    public const string ScenarioBalanceShortfall = "balance_shortfall";

    public static readonly string[] Scenarios =
    [
        ScenarioNormal,
        ScenarioDelayed,
        ScenarioRejected,
        ScenarioSubmitTimeoutFilled,
        ScenarioSubmitTimeoutLost,
        ScenarioStatusUnavailable,
        ScenarioNameMismatch,
        ScenarioBalanceShortfall,
    ];

    private readonly object _gate = new();
    private readonly List<FakeFundingDeposit> _deposits = [];
    private readonly Dictionary<string, FakeFundingPayout> _payouts = new(StringComparer.Ordinal);

    public string Scenario { get; set; } = ScenarioNormal;

    /// <summary>Delay before a normal payout reports completed.</summary>
    public TimeSpan CompletionDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Delay for the delayed scenario; defaults to one hour.</summary>
    public TimeSpan DelayedCompletionDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Spy counter proving idempotent submission.</summary>
    public int PayoutSubmitCount { get; private set; }

    public void InjectDeposit(FakeFundingDeposit deposit)
    {
        lock (_gate)
        {
            _deposits.Add(deposit);
        }
    }

    public IReadOnlyList<FakeFundingDeposit> DepositsFor(string providerKey)
    {
        lock (_gate)
        {
            return _deposits.Where(d => d.ProviderKey == providerKey).ToList();
        }
    }

    public void RecordPayout(FakeFundingPayout payout)
    {
        lock (_gate)
        {
            _payouts[payout.WithdrawalKey] = payout;
            PayoutSubmitCount++;
        }
    }

    public bool TryGetPayout(string withdrawalKey, out FakeFundingPayout? payout)
    {
        lock (_gate)
        {
            return _payouts.TryGetValue(withdrawalKey, out payout);
        }
    }

    public bool HasPayout(string withdrawalKey)
    {
        lock (_gate)
        {
            return _payouts.ContainsKey(withdrawalKey);
        }
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using AskVest.Modules.Funding.Configuration;

namespace AskVest.Modules.Funding.Providers;

/// <summary>
/// Deterministic funding provider for development and tests. Instructions
/// derive from the user reference, deposits arrive through injected items
/// surfaced by polling or signed webhooks, payouts complete after a delay,
/// and the settlement balance comes from configuration. Webhooks use the
/// shared HMAC scheme in <see cref="FundingWebhookSignature"/>.
/// </summary>
internal sealed class FakeFundingProvider : IFundingProvider
{
    private readonly FakeFundingState _state;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _tolerance;
    private readonly IReadOnlyDictionary<string, decimal> _balances;

    public FakeFundingProvider(
        string key,
        string webhookSecret,
        FakeFundingState state,
        TimeProvider timeProvider,
        TimeSpan tolerance,
        IReadOnlyDictionary<string, decimal> balances)
    {
        Key = key;
        WebhookSecret = webhookSecret;
        _state = state;
        _timeProvider = timeProvider;
        _tolerance = tolerance;
        _balances = balances;
    }

    public string Key { get; }

    private string WebhookSecret { get; }

    public async Task<FundingInstruction> GetOrCreateInstructionAsync(
        string userReference,
        string currency,
        CancellationToken cancellationToken = default)
    {
        var reference = $"fpi-{StableHash($"{Key}|{userReference}|{currency}")[..24]}";
        var json = JsonSerializer.Serialize(new
        {
            reference,
            fields = new Dictionary<string, string>
            {
                ["accountName"] = "AskVest User",
                ["bankName"] = "Fake Settlement Bank",
                ["accountNumber"] = reference,
                ["currency"] = currency,
            },
        });
        await Task.CompletedTask;
        return new FundingInstruction(reference, json);
    }

    public Task<FundingDepositPage> ListDepositsAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var items = _state.DepositsFor(Key)
            .OrderBy(deposit => deposit.ProviderDepositId, StringComparer.Ordinal)
            .Where(deposit => cursor is null
                || string.CompareOrdinal(deposit.ProviderDepositId, cursor) > 0)
            .Take(limit + 1)
            .Select(ToItem)
            .ToList();
        var page = items.Take(limit).ToList();
        var next = items.Count > limit ? page[^1].ProviderDepositId : null;
        return Task.FromResult(new FundingDepositPage(page, next));
    }

    public Task<FundingBeneficiaryResult> AddBeneficiaryAsync(
        string userReference,
        string currency,
        IReadOnlyDictionary<string, string> details,
        CancellationToken cancellationToken = default)
    {
        if (details.Count == 0)
        {
            throw new FundingProviderException(FundingProviderFailureKind.Rejected, "beneficiary_details_empty");
        }

        var reference = $"fpb-{StableHash($"{Key}|{userReference}|{currency}|{string.Join('|', details.OrderBy(kv => kv.Key).Select(kv => kv.Value))}")[..24]}";
        var nameMatch = _state.Scenario != FakeFundingState.ScenarioNameMismatch;
        return Task.FromResult(new FundingBeneficiaryResult(
            reference,
            Mask(details),
            nameMatch));
    }

    public Task RequestPayoutAsync(
        string withdrawalKey,
        string beneficiaryReference,
        string currency,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        if (_state.HasPayout(withdrawalKey))
        {
            // Idempotent by our key: a repeat returns the original ack.
            return Task.CompletedTask;
        }

        switch (_state.Scenario)
        {
            case FakeFundingState.ScenarioRejected:
                throw new FundingProviderException(FundingProviderFailureKind.Rejected, "payout_rejected");
            case FakeFundingState.ScenarioSubmitTimeoutFilled:
                _state.RecordPayout(new FakeFundingPayout
                {
                    WithdrawalKey = withdrawalKey,
                    BeneficiaryReference = beneficiaryReference,
                    Currency = currency,
                    Amount = amount,
                    SubmittedAt = _timeProvider.GetUtcNow(),
                    Filled = true,
                });
                throw new FundingProviderException(FundingProviderFailureKind.Transient, "submit_timeout");
            case FakeFundingState.ScenarioSubmitTimeoutLost:
                throw new FundingProviderException(FundingProviderFailureKind.Transient, "submit_timeout");
            default:
                _state.RecordPayout(new FakeFundingPayout
                {
                    WithdrawalKey = withdrawalKey,
                    BeneficiaryReference = beneficiaryReference,
                    Currency = currency,
                    Amount = amount,
                    SubmittedAt = _timeProvider.GetUtcNow(),
                    Filled = true,
                });
                return Task.CompletedTask;
        }
    }

    public Task<FundingPayoutStatus> GetPayoutStatusAsync(
        string withdrawalKey,
        CancellationToken cancellationToken = default)
    {
        if (_state.Scenario == FakeFundingState.ScenarioStatusUnavailable)
        {
            throw new FundingProviderException(FundingProviderFailureKind.Transient, "status_unavailable");
        }

        if (!_state.TryGetPayout(withdrawalKey, out var payout) || payout is null || !payout.Filled)
        {
            return Task.FromResult(new FundingPayoutStatus(FundingPayoutReportState.NotFound, null));
        }

        var now = _timeProvider.GetUtcNow();
        var delay = _state.Scenario == FakeFundingState.ScenarioDelayed
            ? _state.DelayedCompletionDelay
            : _state.CompletionDelay;
        var effective = payout.State == FundingPayoutReportState.Pending && now - payout.SubmittedAt >= delay
            ? FundingPayoutReportState.Completed
            : payout.State;
        return Task.FromResult(new FundingPayoutStatus(effective, payout.DetailCode));
    }

    public Task<decimal> GetSettlementBalanceAsync(
        string currency,
        CancellationToken cancellationToken = default)
    {
        var balance = _balances.TryGetValue(currency, out var configured) ? configured : 0m;
        if (_state.Scenario == FakeFundingState.ScenarioBalanceShortfall)
        {
            balance = Math.Max(0m, balance - 0.01m);
        }

        return Task.FromResult(balance);
    }

    public FundingWebhookParseResult ParseWebhook(
        string rawBody,
        IReadOnlyDictionary<string, string?> headers,
        DateTimeOffset now)
    {
        if (!headers.TryGetValue(FundingWebhookSignature.TimestampHeader, out var timestamp)
            || !headers.TryGetValue(FundingWebhookSignature.SignatureHeader, out var signature))
        {
            return new FundingWebhookParseResult(FundingWebhookParseStatus.MissingOrStaleTimestamp, null);
        }

        if (!FundingWebhookSignature.Verify(WebhookSecret, timestamp, rawBody, signature, _tolerance, now))
        {
            // A stale timestamp and a bad signature are distinct failures;
            // check staleness first like the KYC provider does.
            if (!long.TryParse(timestamp, out var unixSeconds)
                || (now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds)).Duration() > _tolerance)
            {
                return new FundingWebhookParseResult(FundingWebhookParseStatus.MissingOrStaleTimestamp, null);
            }

            return new FundingWebhookParseResult(FundingWebhookParseStatus.InvalidSignature, null);
        }

        return ParseBody(rawBody);
    }

    internal void InjectDeposit(FakeFundingDeposit deposit) => _state.InjectDeposit(deposit);

    internal FakeFundingState State => _state;

    private static FundingDepositItem ToItem(FakeFundingDeposit deposit)
        => new(
            deposit.ProviderDepositId,
            deposit.AccountReference,
            deposit.Currency,
            deposit.Amount,
            deposit.ProviderFee,
            deposit.PayerVerified,
            deposit.State,
            deposit.ObservedAt);

    private static string Mask(IReadOnlyDictionary<string, string> details)
    {
        var parts = details.Values.Select(value => value.Length <= 4 ? "****" : $"****{value[^4..]}");
        return string.Join(" ", parts);
    }

    private static string StableHash(string input)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();

    private static FundingWebhookParseResult ParseBody(string rawBody)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            var eventId = root.GetProperty("eventId").GetString();
            var kind = root.GetProperty("kind").GetString();
            if (string.IsNullOrWhiteSpace(eventId) || kind is not ("deposit" or "payout"))
            {
                return new FundingWebhookParseResult(FundingWebhookParseStatus.Malformed, null);
            }

            if (kind == "deposit")
            {
                var deposit = root.GetProperty("deposit");
                var stateText = deposit.GetProperty("state").GetString();
                var state = stateText switch
                {
                    "pending" => FundingDepositReportState.Pending,
                    "completed" => FundingDepositReportState.Completed,
                    "failed" => FundingDepositReportState.Failed,
                    "reversed" => FundingDepositReportState.Reversed,
                    _ => (FundingDepositReportState?)null,
                };
                if (state is null
                    || !TryDecimal(deposit, "amount", out var amount)
                    || !TryDecimal(deposit, "providerFee", out var providerFee))
                {
                    return new FundingWebhookParseResult(FundingWebhookParseStatus.Malformed, null);
                }

                var item = new FundingDepositItem(
                    Required(deposit, "providerDepositId"),
                    Required(deposit, "accountReference"),
                    Required(deposit, "currency"),
                    amount,
                    providerFee,
                    deposit.TryGetProperty("payerVerified", out var pv) && pv.ValueKind == JsonValueKind.True,
                    state.Value,
                    OptionalTime(deposit, "observedAt") ?? DateTimeOffset.MinValue);
                return new FundingWebhookParseResult(
                    FundingWebhookParseStatus.Ok,
                    new FundingWebhookEventData(eventId, FundingWebhookKind.Deposit, item, null, null, null));
            }

            var payoutStateText = root.GetProperty("state").GetString();
            var payoutState = payoutStateText switch
            {
                "pending" => FundingPayoutReportState.Pending,
                "completed" => FundingPayoutReportState.Completed,
                "failed" => FundingPayoutReportState.Failed,
                _ => (FundingPayoutReportState?)null,
            };
            if (payoutState is null)
            {
                return new FundingWebhookParseResult(FundingWebhookParseStatus.Malformed, null);
            }

            return new FundingWebhookParseResult(
                FundingWebhookParseStatus.Ok,
                new FundingWebhookEventData(
                    eventId,
                    FundingWebhookKind.Payout,
                    null,
                    Required(root, "payoutKey"),
                    payoutState.Value,
                    root.TryGetProperty("detailCode", out var dc) && dc.ValueKind == JsonValueKind.String
                        ? dc.GetString()
                        : null));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new FundingWebhookParseResult(FundingWebhookParseStatus.Malformed, null);
        }
    }

    private static string Required(JsonElement element, string name)
        => element.GetProperty(name).GetString()
            ?? throw new JsonException($"Missing {name}.");

    private static bool TryDecimal(JsonElement element, string name, out decimal value)
    {
        value = 0m;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.String
            ? decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            : property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out value);
    }

    private static DateTimeOffset? OptionalTime(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return null;
        }

        return parsed;
    }
}

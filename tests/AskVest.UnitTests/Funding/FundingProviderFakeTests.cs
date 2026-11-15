using System.Text.Json;
using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Providers;
using AskVest.Modules.Funding.Services;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Funding;

public sealed class FundingProviderFakeTests
{
    private const string Secret = "test-webhook-secret-with-32-plus-bytes!!";

    private static FakeFundingProvider Provider(FakeFundingState? state = null)
        => new(
            "fake-funding",
            Secret,
            state ?? new FakeFundingState(),
            TimeProvider.System,
            TimeSpan.FromMinutes(5),
            new Dictionary<string, decimal> { ["USD"] = 1_000_000m });

    private static string Sign(string body, long unixSeconds)
        => FundingWebhookSignature.Sign(Secret, unixSeconds.ToString(), body);

    private static string DepositBody(string eventId, string state = "completed")
        => JsonSerializer.Serialize(new
        {
            eventId,
            kind = "deposit",
            deposit = new
            {
                providerDepositId = "dep-1",
                accountReference = "fpi-abc",
                currency = "USD",
                amount = "10.50",
                providerFee = "0.00",
                payerVerified = true,
                state,
                observedAt = "2030-01-01T00:00:00Z",
            },
        });

    [Fact]
    public async Task Instruction_Is_Deterministic_Per_User_And_Currency()
    {
        var provider = Provider();
        var first = await provider.GetOrCreateInstructionAsync("user-1", "USD", TestContext.Current.CancellationToken);
        var second = await provider.GetOrCreateInstructionAsync("user-1", "USD", TestContext.Current.CancellationToken);
        var other = await provider.GetOrCreateInstructionAsync("user-1", "NGN", TestContext.Current.CancellationToken);

        first.ProviderReference.ShouldBe(second.ProviderReference);
        first.ProviderReference.ShouldNotBe(other.ProviderReference);
        first.InstructionJson.ShouldContain(first.ProviderReference);
    }

    [Fact]
    public async Task Deposits_List_Pages_By_Cursor()
    {
        var state = new FakeFundingState();
        var provider = Provider(state);
        state.InjectDeposit(new FakeFundingDeposit
        {
            ProviderKey = "fake-funding",
            ProviderDepositId = "dep-1",
            AccountReference = "fpi-a",
            Currency = "USD",
            Amount = 10m,
            ObservedAt = DateTimeOffset.UnixEpoch,
        });
        state.InjectDeposit(new FakeFundingDeposit
        {
            ProviderKey = "fake-funding",
            ProviderDepositId = "dep-2",
            AccountReference = "fpi-a",
            Currency = "USD",
            Amount = 20m,
            ObservedAt = DateTimeOffset.UnixEpoch,
        });
        state.InjectDeposit(new FakeFundingDeposit
        {
            ProviderKey = "other-provider",
            ProviderDepositId = "dep-9",
            AccountReference = "fpi-a",
            Currency = "USD",
            Amount = 30m,
            ObservedAt = DateTimeOffset.UnixEpoch,
        });

        var page = await provider.ListDepositsAsync(null, 1, TestContext.Current.CancellationToken);
        page.Items.Count.ShouldBe(1);
        page.Items[0].ProviderDepositId.ShouldBe("dep-1");
        page.NextCursor.ShouldBe("dep-1");

        var rest = await provider.ListDepositsAsync(page.NextCursor, 10, TestContext.Current.CancellationToken);
        rest.Items.Select(i => i.ProviderDepositId).ShouldBe(["dep-2"]);
        rest.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Payout_Is_Idempotent_By_Withdrawal_Key()
    {
        var state = new FakeFundingState { CompletionDelay = TimeSpan.Zero };
        var provider = Provider(state);
        await provider.RequestPayoutAsync("w-1", "fpb-1", "USD", 50m, TestContext.Current.CancellationToken);
        await provider.RequestPayoutAsync("w-1", "fpb-1", "USD", 50m, TestContext.Current.CancellationToken);

        state.PayoutSubmitCount.ShouldBe(1);
        var status = await provider.GetPayoutStatusAsync("w-1", TestContext.Current.CancellationToken);
        status.State.ShouldBe(FundingPayoutReportState.Completed);
        (await provider.GetPayoutStatusAsync("missing", TestContext.Current.CancellationToken))
            .State.ShouldBe(FundingPayoutReportState.NotFound);
    }

    [Theory]
    [InlineData(FakeFundingState.ScenarioRejected, FundingProviderFailureKind.Rejected)]
    [InlineData(FakeFundingState.ScenarioSubmitTimeoutFilled, FundingProviderFailureKind.Transient)]
    [InlineData(FakeFundingState.ScenarioSubmitTimeoutLost, FundingProviderFailureKind.Transient)]
    public async Task Payout_Scenarios(string scenario, FundingProviderFailureKind expected)
    {
        var state = new FakeFundingState { Scenario = scenario };
        var provider = Provider(state);
        var exception = await Should.ThrowAsync<FundingProviderException>(() =>
            provider.RequestPayoutAsync("w-2", "fpb-1", "USD", 50m, TestContext.Current.CancellationToken));
        exception.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task SubmitTimeoutFilled_Still_Settles_Later()
    {
        var state = new FakeFundingState
        {
            Scenario = FakeFundingState.ScenarioSubmitTimeoutFilled,
            CompletionDelay = TimeSpan.Zero,
        };
        var provider = Provider(state);
        await Should.ThrowAsync<FundingProviderException>(() =>
            provider.RequestPayoutAsync("w-3", "fpb-1", "USD", 50m, TestContext.Current.CancellationToken));

        var status = await provider.GetPayoutStatusAsync("w-3", TestContext.Current.CancellationToken);
        status.State.ShouldBe(FundingPayoutReportState.Completed);
    }

    [Fact]
    public async Task StatusUnavailable_And_Delayed_Scenarios()
    {
        var unavailable = new FakeFundingState { Scenario = FakeFundingState.ScenarioStatusUnavailable };
        var provider = Provider(unavailable);
        await Should.ThrowAsync<FundingProviderException>(() =>
            provider.GetPayoutStatusAsync("w-4", TestContext.Current.CancellationToken));

        var delayed = new FakeFundingState
        {
            Scenario = FakeFundingState.ScenarioDelayed,
            DelayedCompletionDelay = TimeSpan.FromHours(1),
        };
        var delayedProvider = Provider(delayed);
        await delayedProvider.RequestPayoutAsync("w-5", "fpb-1", "USD", 50m, TestContext.Current.CancellationToken);
        (await delayedProvider.GetPayoutStatusAsync("w-5", TestContext.Current.CancellationToken))
            .State.ShouldBe(FundingPayoutReportState.Pending);
    }

    [Fact]
    public async Task BalanceShortfall_Reports_Less_Than_Configured()
    {
        var provider = Provider(new FakeFundingState { Scenario = FakeFundingState.ScenarioBalanceShortfall });
        (await provider.GetSettlementBalanceAsync("USD", TestContext.Current.CancellationToken))
            .ShouldBe(999_999.99m);
        (await Provider().GetSettlementBalanceAsync("USD", TestContext.Current.CancellationToken))
            .ShouldBe(1_000_000m);
    }

    [Fact]
    public async Task Beneficiary_Name_Mismatch_Scenario()
    {
        var provider = Provider(new FakeFundingState { Scenario = FakeFundingState.ScenarioNameMismatch });
        var result = await provider.AddBeneficiaryAsync(
            "user-1", "USD", new Dictionary<string, string> { ["account"] = "1234" },
            TestContext.Current.CancellationToken);
        result.NameMatch.ShouldBeFalse();
        result.MaskedDisplay.ShouldNotContain("1234");
    }

    [Fact]
    public void Webhook_Valid_Signature_Parses_Deposit()
    {
        var provider = Provider();
        var body = DepositBody("evt-1");
        var now = TimeProvider.System.GetUtcNow();
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var headers = new Dictionary<string, string?>
        {
            [FundingWebhookSignature.TimestampHeader] = timestamp,
            [FundingWebhookSignature.SignatureHeader] = Sign(body, long.Parse(timestamp)),
        };

        var result = provider.ParseWebhook(body, headers, now);
        result.Status.ShouldBe(FundingWebhookParseStatus.Ok);
        result.Event!.Kind.ShouldBe(FundingWebhookKind.Deposit);
        result.Event.Deposit!.ProviderDepositId.ShouldBe("dep-1");
        result.Event.Deposit.Amount.ShouldBe(10.50m);
    }

    [Fact]
    public void Webhook_Tampered_Body_Fails_Signature()
    {
        var provider = Provider();
        var body = DepositBody("evt-1");
        var now = TimeProvider.System.GetUtcNow();
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var headers = new Dictionary<string, string?>
        {
            [FundingWebhookSignature.TimestampHeader] = timestamp,
            [FundingWebhookSignature.SignatureHeader] = Sign(body, long.Parse(timestamp)),
        };

        var tampered = body.Replace("10.50", "99.99");
        provider.ParseWebhook(tampered, headers, now)
            .Status.ShouldBe(FundingWebhookParseStatus.InvalidSignature);
    }

    [Fact]
    public void Webhook_Stale_Timestamp_Fails()
    {
        var provider = Provider();
        var body = DepositBody("evt-1");
        var now = TimeProvider.System.GetUtcNow();
        var stale = now.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        var headers = new Dictionary<string, string?>
        {
            [FundingWebhookSignature.TimestampHeader] = stale,
            [FundingWebhookSignature.SignatureHeader] = Sign(body, long.Parse(stale)),
        };

        provider.ParseWebhook(body, headers, now)
            .Status.ShouldBe(FundingWebhookParseStatus.MissingOrStaleTimestamp);
    }

    [Fact]
    public void Webhook_Missing_Signature_Fails()
    {
        var provider = Provider();
        var body = DepositBody("evt-1");
        provider.ParseWebhook(body, new Dictionary<string, string?>(), TimeProvider.System.GetUtcNow())
            .Status.ShouldBe(FundingWebhookParseStatus.MissingOrStaleTimestamp);
    }

    [Fact]
    public void Webhook_Payout_Event_Parses()
    {
        var provider = Provider();
        var body = JsonSerializer.Serialize(new
        {
            eventId = "evt-p1",
            kind = "payout",
            payoutKey = "01a00000-0000-7000-8000-000000000001",
            state = "completed",
        });
        var now = TimeProvider.System.GetUtcNow();
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var headers = new Dictionary<string, string?>
        {
            [FundingWebhookSignature.TimestampHeader] = timestamp,
            [FundingWebhookSignature.SignatureHeader] = Sign(body, long.Parse(timestamp)),
        };

        var result = provider.ParseWebhook(body, headers, now);
        result.Status.ShouldBe(FundingWebhookParseStatus.Ok);
        result.Event!.Kind.ShouldBe(FundingWebhookKind.Payout);
        result.Event.PayoutState.ShouldBe(FundingPayoutReportState.Completed);
    }

    [Fact]
    public async Task Fx_Fake_Scenarios()
    {
        var options = Options.Create(new FundingOptions());
        options.Value.FxRates.Add(new FundingFxRateOption { From = "USD", To = "NGN", Rate = 1500m });
        var fx = new FakeFxRateProvider(options, TimeProvider.System);

        var rate = await fx.GetMidRateAsync("USD:NGN", TestContext.Current.CancellationToken);
        rate.MidRate.ShouldBe(1500m);
        rate.Source.ShouldBe("fake");

        fx.Scenario = FakeFxRateProvider.ScenarioStale;
        var stale = await fx.GetMidRateAsync("USD:NGN", TestContext.Current.CancellationToken);
        (TimeProvider.System.GetUtcNow() - stale.AsOf).TotalSeconds.ShouldBeGreaterThan(60);

        fx.Scenario = FakeFxRateProvider.ScenarioError;
        await Should.ThrowAsync<FundingProviderException>(() =>
            fx.GetMidRateAsync("USD:NGN", TestContext.Current.CancellationToken));

        fx.Scenario = FakeFxRateProvider.ScenarioJump;
        var jumped = await fx.GetMidRateAsync("USD:NGN", TestContext.Current.CancellationToken);
        jumped.MidRate.ShouldBe(2250m);
    }
}

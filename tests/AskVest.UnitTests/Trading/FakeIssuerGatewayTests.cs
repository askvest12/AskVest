using AskVest.Modules.MarketData.Contracts;
using AskVest.Modules.Trading.Configuration;
using AskVest.Modules.Trading.Domain;
using AskVest.Modules.Trading.Gateway;
using AskVest.UnitTests.Assistant;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Trading;

public sealed class FakeIssuerGatewayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static GatewayTokenReference Token(int decimals = 6)
        => new(
            Guid.Parse("019a0000-0000-7000-8000-0000000000aa"),
            "INST-1",
            "eip155:1",
            "0xcanonical",
            "eip155:1/erc20:0xcanonical",
            "TKN",
            decimals,
            "TKN");

    private static (FakeIssuerGateway Gateway, StubMarketData Market, FakeTimeProvider Clock, TradingOptions Options) Create(
        string scenario = "normal",
        decimal price = 100m,
        decimal spreadPercent = 0.5m,
        int fillDelaySeconds = 2)
    {
        var options = new TradingOptions
        {
            QuoteLifetimeSeconds = 30,
            Fake =
            {
                Scenario = scenario,
                SpreadPercent = spreadPercent,
                FillDelaySeconds = fillDelaySeconds,
                SubmitDelaySeconds = 0,
                GatewayFeeAmount = 0.25m,
            },
        };
        var market = new StubMarketData();
        market.Prices.Add(new MarketDataPrice(
            "INST-1", price, "USD", Now, 0, "regular", "live", true, "fake", null));
        var clock = new FakeTimeProvider(Now);
        var gateway = new FakeIssuerGateway(
            market,
            Options.Create(options),
            clock);
        return (gateway, market, clock, options);
    }

    private static GatewayQuoteRequest BuyRequest(decimal amount = 100m)
        => new(Token(), OrderSide.Buy, amount, null, "USD");

    private static GatewayQuoteRequest SellRequest(decimal quantity = 2m)
        => new(Token(), OrderSide.Sell, null, quantity, "USD");

    private static GatewaySubmitRequest SubmitRequest(string ourReference = "order-1")
        => new(
            ourReference,
            "fakeq-abc",
            Token(),
            OrderSide.Buy,
            1m,
            100m,
            100m,
            0.25m,
            100.25m,
            "USD");

    [Fact]
    public async Task Normal_Quote_Applies_Spread_And_Expiry()
    {
        var (gateway, _, clock, _) = Create();
        var quote = (await gateway.GetFirmQuoteAsync(BuyRequest(), TestContext.Current.CancellationToken)).Value;
        quote.Price.ShouldBe(100.5m);
        quote.Quantity.ShouldBe(0.995024m);
        quote.GatewayFee.ShouldBe(0.25m);
        quote.SettlementAsset.ShouldBe("USD");
        quote.ExpiresAt.ShouldBe(clock.GetUtcNow() + TimeSpan.FromSeconds(30));
        quote.GatewayQuoteId.ShouldStartWith("fakeq-");
    }

    [Fact]
    public async Task Sell_Quote_Prices_Below_Reference()
    {
        var (gateway, _, _, _) = Create();
        var quote = (await gateway.GetFirmQuoteAsync(SellRequest(), TestContext.Current.CancellationToken)).Value;
        quote.Price.ShouldBe(99.5m);
        quote.Quantity.ShouldBe(2m);
    }

    [Fact]
    public async Task Quote_Is_Deterministic_For_Identical_Requests()
    {
        var (gateway, _, _, _) = Create();
        var first = (await gateway.GetFirmQuoteAsync(BuyRequest(), TestContext.Current.CancellationToken)).Value;
        var second = (await gateway.GetFirmQuoteAsync(BuyRequest(), TestContext.Current.CancellationToken)).Value;
        first.ShouldBe(second);
    }

    [Fact]
    public async Task Quote_Error_Scenario_Fails()
    {
        var (gateway, _, _, _) = Create("quote_error");
        var result = await gateway.GetFirmQuoteAsync(BuyRequest(), TestContext.Current.CancellationToken);
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Short_Quote_Expiry_Scenario_Expires_Soon()
    {
        var (gateway, _, clock, _) = Create("short_quote_expiry");
        var quote = (await gateway.GetFirmQuoteAsync(BuyRequest(), TestContext.Current.CancellationToken)).Value;
        quote.ExpiresAt.ShouldBe(clock.GetUtcNow() + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Missing_Reference_Price_Fails_The_Quote()
    {
        var (gateway, market, _, _) = Create();
        market.Prices.Clear();
        var result = await gateway.GetFirmQuoteAsync(BuyRequest(), TestContext.Current.CancellationToken);
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Normal_Submit_Acknowledges_And_Fills_Exactly()
    {
        var (gateway, _, clock, _) = Create(fillDelaySeconds: 1);
        var submit = await gateway.SubmitOrderAsync(SubmitRequest(), TestContext.Current.CancellationToken);
        submit.Kind.ShouldBe(GatewaySubmitKind.Acknowledged);
        submit.GatewayReference.ShouldBe("fake-order-order-1");

        var early = await gateway.GetOrderStatusAsync("order-1", TestContext.Current.CancellationToken);
        early.Kind.ShouldBe(GatewayStatusKind.Pending);

        clock.Advance(TimeSpan.FromSeconds(2));
        var filled = await gateway.GetOrderStatusAsync("order-1", TestContext.Current.CancellationToken);
        filled.Kind.ShouldBe(GatewayStatusKind.Filled);
        filled.Fill!.Quantity.ShouldBe(1m);
        filled.Fill.Price.ShouldBe(100m);
        filled.Fill.Total.ShouldBe(100.25m);
        filled.Fill.SettlementReference.ShouldBe("fake-settle-order-1");
    }

    [Fact]
    public async Task Rejected_Scenario_Definitively_Rejects()
    {
        var (gateway, _, _, _) = Create("rejected");
        var submit = await gateway.SubmitOrderAsync(SubmitRequest(), TestContext.Current.CancellationToken);
        submit.Kind.ShouldBe(GatewaySubmitKind.Rejected);
        submit.ReasonCode.ShouldBe("gateway_rejected");
    }

    [Fact]
    public async Task Delayed_Fill_Stays_Pending_Until_The_Delay_Passes()
    {
        var (gateway, _, clock, _) = Create("delayed_fill", fillDelaySeconds: 1);
        await gateway.SubmitOrderAsync(SubmitRequest(), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(3));
        (await gateway.GetOrderStatusAsync("order-1", TestContext.Current.CancellationToken)).Kind.ShouldBe(GatewayStatusKind.Pending);
        clock.Advance(TimeSpan.FromSeconds(2));
        (await gateway.GetOrderStatusAsync("order-1", TestContext.Current.CancellationToken)).Kind.ShouldBe(GatewayStatusKind.Filled);
    }

    [Fact]
    public async Task Submit_Timeout_Filled_Stores_The_Order()
    {
        var (gateway, _, clock, _) = Create("submit_timeout_filled", fillDelaySeconds: 1);
        var submit = await gateway.SubmitOrderAsync(SubmitRequest(), TestContext.Current.CancellationToken);
        submit.Kind.ShouldBe(GatewaySubmitKind.Unknown);
        clock.Advance(TimeSpan.FromSeconds(2));
        (await gateway.GetOrderStatusAsync("order-1", TestContext.Current.CancellationToken)).Kind.ShouldBe(GatewayStatusKind.Filled);
    }

    [Fact]
    public async Task Submit_Timeout_Lost_Leaves_No_Order()
    {
        var (gateway, _, _, _) = Create("submit_timeout_lost");
        var submit = await gateway.SubmitOrderAsync(SubmitRequest(), TestContext.Current.CancellationToken);
        submit.Kind.ShouldBe(GatewaySubmitKind.Unknown);
        (await gateway.GetOrderStatusAsync("order-1", TestContext.Current.CancellationToken)).Kind.ShouldBe(GatewayStatusKind.NotFound);
    }

    [Fact]
    public async Task Status_Unavailable_Always_Reports_Unavailable()
    {
        var (gateway, _, _, _) = Create("status_unavailable");
        await gateway.SubmitOrderAsync(SubmitRequest(), TestContext.Current.CancellationToken);
        (await gateway.GetOrderStatusAsync("order-1", TestContext.Current.CancellationToken)).Kind.ShouldBe(GatewayStatusKind.Unavailable);
    }

    [Fact]
    public async Task Fill_Mismatch_Produces_A_Different_Fill()
    {
        var (gateway, _, clock, _) = Create("fill_mismatch", fillDelaySeconds: 1);
        await gateway.SubmitOrderAsync(SubmitRequest(), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));
        var status = await gateway.GetOrderStatusAsync("order-1", TestContext.Current.CancellationToken);
        status.Kind.ShouldBe(GatewayStatusKind.Filled);
        status.Fill!.Price.ShouldBe(100.01m);
    }

    [Fact]
    public async Task Unknown_Key_Reports_NotFound()
    {
        var (gateway, _, _, _) = Create();
        (await gateway.GetOrderStatusAsync("never-submitted", TestContext.Current.CancellationToken)).Kind.ShouldBe(GatewayStatusKind.NotFound);
    }

    [Fact]
    public async Task Duplicate_Submit_Is_Idempotent()
    {
        var (gateway, _, _, _) = Create();
        var first = await gateway.SubmitOrderAsync(SubmitRequest(), TestContext.Current.CancellationToken);
        var second = await gateway.SubmitOrderAsync(SubmitRequest(), TestContext.Current.CancellationToken);
        first.ShouldBe(second);
        gateway.StoredOrderCount.ShouldBe(1);
    }

    [Fact]
    public void SetScenario_Changes_Behavior_At_Runtime()
    {
        var (gateway, _, _, options) = Create();
        options.Fake.Scenario.ShouldBe("normal");
        gateway.SetScenario("rejected");
        options.Fake.Scenario.ShouldBe("rejected");
    }
}

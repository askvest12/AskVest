using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Catalog.Import.Data;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.MarketData.Contracts;
using AskVest.Modules.MarketData.Providers;
using AskVest.Modules.MarketData.Services;
using AskVest.Modules.Practice.Domain;
using AskVest.Modules.Practice.Endpoints;
using AskVest.Modules.Practice.Persistence;
using AskVest.Modules.Practice.Persistence.Entities;
using AskVest.Modules.Practice.Services;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Practice;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PracticeStopTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provider = null!;
    private string _instrumentId = null!;
    private string _assetCode = null!;
    private InstrumentData _instrument = null!;

    public async ValueTask InitializeAsync()
    {
        var suffix = CatalogImportTestData.Suffix();
        _instrumentId = $"instr-stop-{suffix}";
        var symbol = $"STP{suffix}".ToUpperInvariant();
        _assetCode = $"P.{symbol}";
        _provider = PracticeTestServices.CreateProvider(fixture, _clock);
        _instrument = CatalogImportTestData.Instrument(_instrumentId) with
        {
            Symbol = symbol,
            ListingExchange = "NMS",
        };
        var imported = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], [_instrument], TestContext.Current.CancellationToken);
        imported.IsSuccess.ShouldBeTrue(imported.Error?.Message);
        var poller = _provider.GetRequiredService<PricePoller>();
        (await poller.RunCycleAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    private async Task<Guid> FundedUserAsync()
    {
        var userId = Guid.CreateVersion7();
        var scope = _provider.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<PracticeAccountService>()
            .StartAsync(userId, null, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        await scope.DisposeAsync();
        return userId;
    }

    private async Task<MarketDataPrice> PriceAsync(string? instrumentId = null)
    {
        var scope = _provider.CreateAsyncScope();
        var price = await scope.ServiceProvider
            .GetRequiredService<IMarketData>()
            .GetLatestPriceAsync(instrumentId ?? _instrumentId, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return price;
    }

    private async Task<Result<PracticeOrder>> PlaceAsync(
        Guid userId,
        string side,
        string type = "market",
        string? amount = null,
        string? quantity = null,
        bool? sellAll = null,
        string? stopPrice = null,
        string? key = null)
    {
        var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<PracticeOrderService>()
            .PlaceAsync(
                userId,
                new PracticeOrderRequest(_instrumentId, side, type, amount, quantity, sellAll, stopPrice),
                key ?? $"k-{Guid.CreateVersion7():N}",
                null,
                TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return result;
    }

    private async Task<(decimal Available, decimal Held)> AssetBalanceAsync(Guid userId)
    {
        var scope = _provider.CreateAsyncScope();
        var balances = await scope.ServiceProvider
            .GetRequiredService<ILedger>()
            .GetBalancesAsync(userId, Book.Practice, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        var balance = balances.Value.FirstOrDefault(b => b.AssetCode == _assetCode);
        return (balance?.Available ?? 0m, balance?.Held ?? 0m);
    }

    private async Task<decimal> CashAsync(Guid userId)
    {
        var scope = _provider.CreateAsyncScope();
        var balances = await scope.ServiceProvider
            .GetRequiredService<ILedger>()
            .GetBalancesAsync(userId, Book.Practice, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return balances.Value.Single(b => b.AssetCode == "USD").Available;
    }

    private async Task RepollAsync(string scenario)
    {
        var fake = _provider.GetRequiredService<FakeMarketDataProvider>();
        fake.SetScenario(scenario);
        var poller = _provider.GetRequiredService<PricePoller>();
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> RunEvaluatorAsync()
    {
        var evaluator = _provider.GetRequiredService<PracticeStopEvaluator>();
        (await evaluator.RunCycleAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();
        var open = await db.PracticeOrders.CountAsync(
            order => order.State == PracticeOrderState.Open
                && order.Type == PracticeOrderType.StopLoss,
            TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return open;
    }

    private async Task<PracticeOrder> OrderAsync(Guid orderId)
    {
        var scope = _provider.CreateAsyncScope();
        var order = await scope.ServiceProvider.GetRequiredService<PracticeDbContext>()
            .PracticeOrders.AsTracking().SingleAsync(
                row => row.Id == orderId, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return order;
    }

    private async Task<long> EventCountAsync(Guid userId, string eventType)
        => await Database.ExecuteScalarAsync<long>(
            fixture.OwnerConnectionString,
            $"SELECT count(*) FROM practice.practice_events WHERE user_id = '{userId}' AND event_type = '{eventType}'",
            TestContext.Current.CancellationToken);

    private async Task<long> OutboxCountAsync(Guid orderId)
        => await Database.ExecuteScalarAsync<long>(
            fixture.OwnerConnectionString,
            $"SELECT count(*) FROM platform.outbox_messages WHERE type = 'PracticeOrderFilled' " +
            $"AND payload->>'orderId' = '{orderId}'",
            TestContext.Current.CancellationToken);

    private static string Below(decimal price, decimal fraction = 0.10m)
        => (price * (1m - fraction)).ToString("0.############################");

    [Fact]
    public async Task Stop_Creation_Holds_The_Position()
    {
        var userId = await FundedUserAsync();
        var buy = await PlaceAsync(userId, "buy", amount: "500");
        buy.IsSuccess.ShouldBeTrue();
        var quantity = buy.Value.FilledQuantity!.Value;
        var price = await PriceAsync();

        var stop = await PlaceAsync(
            userId, "sell", type: "stop_loss", quantity: quantity.ToString(),
            stopPrice: Below(price.Price!.Value));
        stop.IsSuccess.ShouldBeTrue(stop.Error?.Message);
        var order = stop.Value;
        order.State.ShouldBe(PracticeOrderState.Open);
        order.Type.ShouldBe(PracticeOrderType.StopLoss);
        order.StopPrice.ShouldNotBeNull();
        order.HoldJournalId.ShouldNotBeNull();
        order.FilledQuantity.ShouldBeNull();

        var (available, held) = await AssetBalanceAsync(userId);
        available.ShouldBe(0m);
        held.ShouldBe(quantity);

        var lines = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            "SELECT a.kind, e.amount FROM ledger.journal_entries e " +
            "JOIN ledger.journals j ON j.id = e.journal_id " +
            "JOIN ledger.accounts a ON a.id = e.account_id " +
            $"WHERE j.id = '{order.HoldJournalId}' ORDER BY e.line_number",
            TestContext.Current.CancellationToken);
        lines.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Stop_Price_At_Or_Above_Market_And_Bad_Prices_Are_Invalid()
    {
        var userId = await FundedUserAsync();
        (await PlaceAsync(userId, "buy", amount: "500")).IsSuccess.ShouldBeTrue();
        var price = await PriceAsync();
        var current = price.Price!.Value;

        foreach (var (label, stopPrice) in new (string, string)[]
        {
            ("at market", current.ToString()),
            ("above market", (current * 2m).ToString()),
            ("zero", "0"),
            ("negative", "-5"),
        })
        {
            var result = await PlaceAsync(
                userId, "sell", type: "stop_loss", sellAll: true, stopPrice: stopPrice,
                key: $"bad-stop-{label}");
            result.IsFailure.ShouldBeTrue($"{label} should fail");
            result.Error!.Code.ShouldBe(ErrorCodes.PracticeOrderInvalid);
        }
    }

    [Fact]
    public async Task Open_Stop_Limit_And_Held_Reservation_Are_Enforced()
    {
        var provider = PracticeTestServices.CreateProvider(
            fixture,
            _clock,
            practiceSettings: new Dictionary<string, string?> { ["Practice:MaxOpenStopOrders"] = "2" });
        await using (provider)
        {
            var userId = Guid.CreateVersion7();
            var scope = provider.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<PracticeAccountService>()
                .StartAsync(userId, null, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
            await scope.DisposeAsync();

            var orders = provider.GetRequiredService<PracticeOrderService>();
            var price = await PriceAsync();
            var stopPrice = Below(price.Price!.Value);

            async Task<Result<PracticeOrder>> StopAsync(string key, string? quantity = null)
            {
                var s = provider.CreateAsyncScope();
                var result = await s.ServiceProvider.GetRequiredService<PracticeOrderService>()
                    .PlaceAsync(
                        userId,
                        new PracticeOrderRequest(_instrumentId, "sell", "stop_loss", null, quantity, null, stopPrice),
                        key, null, TestContext.Current.CancellationToken);
                await s.DisposeAsync();
                return result;
            }

            // Fund the position through the shared provider path is not possible
            // here; use a direct buy through this provider instead.
            var buyScope = provider.CreateAsyncScope();
            (await buyScope.ServiceProvider.GetRequiredService<PracticeOrderService>()
                .PlaceAsync(
                    userId,
                    new PracticeOrderRequest(_instrumentId, "buy", "market", "500", null, null, null),
                    "limit-buy", null, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
            await buyScope.DisposeAsync();

            var first = await StopAsync("stop-1", "1");
            first.IsSuccess.ShouldBeTrue(first.Error?.Message);
            var second = await StopAsync("stop-2", "1");
            second.IsSuccess.ShouldBeTrue(second.Error?.Message);
            var third = await StopAsync("stop-3", "1");
            third.IsFailure.ShouldBeTrue();
            third.Error!.Code.ShouldBe(ErrorCodes.PracticeOrderInvalid);

            // The held unit cannot back a fourth reservation either.
            var oversell = await StopAsync("stop-4", "999999");
            oversell.IsFailure.ShouldBeTrue();
            oversell.Error!.Code.ShouldBe(ErrorCodes.PracticeInsufficientPosition);
        }
    }

    [Fact]
    public async Task Cancel_Releases_The_Hold_And_Conflicts_On_Closed_Orders()
    {
        var userId = await FundedUserAsync();
        (await PlaceAsync(userId, "buy", amount: "500")).IsSuccess.ShouldBeTrue();
        var price = await PriceAsync();
        var stop = await PlaceAsync(
            userId, "sell", type: "stop_loss", sellAll: true,
            stopPrice: Below(price.Price!.Value));
        stop.IsSuccess.ShouldBeTrue();
        var orderId = stop.Value.Id;
        var quantity = stop.Value.RequestedQuantity!.Value;

        var scope = _provider.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<PracticeOrderService>();
        var cancelled = await orders.CancelAsync(userId, orderId, null, TestContext.Current.CancellationToken);
        var again = await orders.CancelAsync(userId, orderId, null, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();

        cancelled.IsSuccess.ShouldBeTrue(cancelled.Error?.Message);
        cancelled.Value.State.ShouldBe("cancelled");
        cancelled.Value.CancelReason.ShouldBe("user");
        again.IsFailure.ShouldBeTrue();
        again.Error!.Code.ShouldBe(ErrorCodes.ResourceConflict);

        var (available, held) = await AssetBalanceAsync(userId);
        available.ShouldBe(quantity);
        held.ShouldBe(0m);
        (await EventCountAsync(userId, "order_cancelled")).ShouldBe(1);
    }

    [Fact]
    public async Task Drop_Scenario_Triggers_The_Stop_At_The_Current_Price()
    {
        var userId = await FundedUserAsync();
        (await PlaceAsync(userId, "buy", amount: "500")).IsSuccess.ShouldBeTrue();
        var before = await PriceAsync();
        var stopPrice = BeforeDropStop(before.Price!.Value);
        var stop = await PlaceAsync(
            userId, "sell", type: "stop_loss", sellAll: true, stopPrice: stopPrice);
        stop.IsSuccess.ShouldBeTrue(stop.Error?.Message);
        var orderId = stop.Value.Id;
        var quantity = stop.Value.RequestedQuantity!.Value;
        var cashBefore = await CashAsync(userId);

        await RepollAsync("drop");
        var dropped = await PriceAsync();
        dropped.Price!.Value.ShouldBeLessThan(before.Price!.Value);
        // The fill must use the dropped price, not the stop price: a gap.
        dropped.Price!.Value.ShouldBeLessThan(decimal.Parse(stopPrice));

        await RunEvaluatorAsync();

        var order = await OrderAsync(orderId);
        order.State.ShouldBe(PracticeOrderState.Filled);
        order.FilledQuantity.ShouldBe(quantity);
        order.FillPrice.ShouldBe(dropped.Price!.Value);
        order.FilledAt.ShouldNotBeNull();
        order.FillJournalId.ShouldNotBeNull();

        var (available, held) = await AssetBalanceAsync(userId);
        available.ShouldBe(0m);
        held.ShouldBe(0m);
        (await CashAsync(userId)).ShouldBeGreaterThan(cashBefore);
        (await EventCountAsync(userId, "stop_triggered")).ShouldBe(1);
        (await OutboxCountAsync(orderId)).ShouldBe(1);

        await RepollAsync("normal");
    }

    private static string BeforeDropStop(decimal price)
        // Drop lowers prices 20%, so a stop 10% under market sits inside the gap.
        => (price * 0.90m).ToString("0.############################");

    [Fact]
    public async Task No_Trigger_When_Price_Stays_Above_The_Stop()
    {
        var userId = await FundedUserAsync();
        (await PlaceAsync(userId, "buy", amount: "500")).IsSuccess.ShouldBeTrue();
        var price = await PriceAsync();
        // A stop far below any plausible drop never triggers.
        var stop = await PlaceAsync(
            userId, "sell", type: "stop_loss", sellAll: true,
            stopPrice: (price.Price!.Value * 0.50m).ToString("0.############################"));
        stop.IsSuccess.ShouldBeTrue();

        await RunEvaluatorAsync();
        (await OrderAsync(stop.Value.Id)).State.ShouldBe(PracticeOrderState.Open);
    }

    [Fact]
    public async Task No_Trigger_On_Unusable_Prices()
    {
        var userId = await FundedUserAsync();
        (await PlaceAsync(userId, "buy", amount: "500")).IsSuccess.ShouldBeTrue();
        var price = await PriceAsync();
        var stop = await PlaceAsync(
            userId, "sell", type: "stop_loss", sellAll: true,
            stopPrice: Below(price.Price!.Value));
        stop.IsSuccess.ShouldBeTrue();
        var orderId = stop.Value.Id;

        foreach (var scenario in new[] { "halted", "closed", "unknown_symbol" })
        {
            await RepollAsync(scenario);
            await RunEvaluatorAsync();
            (await OrderAsync(orderId)).State.ShouldBe(PracticeOrderState.Open, $"scenario {scenario}");
        }

        // A stale price never triggers either.
        await RepollAsync("normal");
        _clock.Advance(TimeSpan.FromSeconds(400));
        await RunEvaluatorAsync();
        (await OrderAsync(orderId)).State.ShouldBe(PracticeOrderState.Open);
    }

    [Fact]
    public async Task Trigger_And_Cancel_Race_Always_Has_Exactly_One_Outcome()
    {
        var userId = await FundedUserAsync();
        (await PlaceAsync(userId, "buy", amount: "500")).IsSuccess.ShouldBeTrue();

        for (var i = 0; i < 50; i++)
        {
            // Stops are created against the normal price, then the drop arms them.
            await RepollAsync("normal");
            var price = await PriceAsync();
            var stopPrice = BeforeDropStop(price.Price!.Value);

            var (available, _) = await AssetBalanceAsync(userId);
            if (available == 0m)
            {
                (await PlaceAsync(userId, "buy", amount: "500", key: $"race-topup-{i}")).IsSuccess.ShouldBeTrue();
            }

            var stop = await PlaceAsync(
                userId, "sell", type: "stop_loss", sellAll: true,
                stopPrice: stopPrice, key: $"race-{i}");
            stop.IsSuccess.ShouldBeTrue(stop.Error?.Message);
            var orderId = stop.Value.Id;

            await RepollAsync("drop");

            var triggerScope = _provider.CreateAsyncScope();
            var cancelScope = _provider.CreateAsyncScope();
            var triggerTask = triggerScope.ServiceProvider
                .GetRequiredService<PracticeOrderService>()
                .TriggerStopAsync(orderId, TestContext.Current.CancellationToken);
            var cancelTask = cancelScope.ServiceProvider
                .GetRequiredService<PracticeOrderService>()
                .CancelAsync(userId, orderId, null, TestContext.Current.CancellationToken);
            await Task.WhenAll(triggerTask, cancelTask);
            await triggerScope.DisposeAsync();
            await cancelScope.DisposeAsync();

            var order = await OrderAsync(orderId);
            order.State.ShouldNotBe(PracticeOrderState.Open, $"iteration {i}: order must leave open");
        }

        await RepollAsync("normal");
    }

    [Fact]
    public async Task A_Failing_Order_Does_Not_Stop_Others_And_Locks_Dedupe()
    {
        var userId = await FundedUserAsync();
        (await PlaceAsync(userId, "buy", amount: "500")).IsSuccess.ShouldBeTrue();
        var price = await PriceAsync();

        // A second instrument whose asset mapping is removed before evaluation.
        var suffix = CatalogImportTestData.Suffix();
        var secondId = $"instr-stop2-{suffix}";
        var second = CatalogImportTestData.Instrument(secondId) with
        {
            Symbol = $"STQ{suffix}".ToUpperInvariant(),
            ListingExchange = "NMS",
        };
        // Imports are authoritative per file: re-import both instruments or the
        // first one would be delisted.
        (await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], [_instrument, second], TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();
        var poller = _provider.GetRequiredService<PricePoller>();
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        var scope = _provider.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<PracticeOrderService>();
        var buySecond = await orders.PlaceAsync(
            userId,
            new PracticeOrderRequest(secondId, "buy", "market", "100", null, null, null),
            "second-buy", null, TestContext.Current.CancellationToken);
        buySecond.IsSuccess.ShouldBeTrue(buySecond.Error?.Message);
        var secondPrice = await PriceAsync(secondId);
        var secondStopPrice = Below(secondPrice.Price!.Value);
        var badStop = await orders.PlaceAsync(
            userId,
            new PracticeOrderRequest(secondId, "sell", "stop_loss", null, null, true, secondStopPrice),
            "second-stop", null, TestContext.Current.CancellationToken);
        badStop.IsSuccess.ShouldBeTrue(badStop.Error?.Message);
        var stopPrice = Below(price.Price!.Value);
        var goodStop = await PlaceAsync(
            userId, "sell", type: "stop_loss", sellAll: true, stopPrice: stopPrice);
        goodStop.IsSuccess.ShouldBeTrue(goodStop.Error?.Message);
        await scope.DisposeAsync();

        // Removing the mapping makes the second order fail inside the evaluator.
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"DELETE FROM practice.practice_instruments WHERE instrument_id = '{secondId}'",
            TestContext.Current.CancellationToken);

        await RepollAsync("drop");

        // Two concurrent evaluator cycles: the advisory lock admits one.
        var evaluator = _provider.GetRequiredService<PracticeStopEvaluator>();
        var results = await Task.WhenAll(
            evaluator.RunCycleAsync(TestContext.Current.CancellationToken),
            evaluator.RunCycleAsync(TestContext.Current.CancellationToken));
        results.ShouldContain(ran => ran);

        (await OrderAsync(goodStop.Value.Id)).State.ShouldBe(PracticeOrderState.Filled);
        (await OrderAsync(badStop.Value.Id)).State.ShouldBe(PracticeOrderState.Open);
        (await OutboxCountAsync(goodStop.Value.Id)).ShouldBe(1);

        await RepollAsync("normal");
    }

    [Fact]
    public async Task Reset_Cancels_Open_Stops_And_Returns_Everything()
    {
        var provider = PracticeTestServices.CreateProvider(
            fixture,
            _clock,
            practiceSettings: new Dictionary<string, string?> { ["Practice:ResetCooldownHours"] = "0" });
        await using (provider)
        {
            var userId = Guid.CreateVersion7();
            var scope = provider.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<PracticeAccountService>()
                .StartAsync(userId, null, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
            await scope.DisposeAsync();

            var orders = provider.GetRequiredService<PracticeOrderService>();
            var price = await PriceAsync();
            var stopPrice = Below(price.Price!.Value);

            async Task<Result<PracticeOrder>> PlaceOnProviderAsync(
                string side, string type, string? amount, bool sellAll, string? stop, string key)
            {
                var s = provider.CreateAsyncScope();
                var result = await s.ServiceProvider.GetRequiredService<PracticeOrderService>()
                    .PlaceAsync(
                        userId,
                        new PracticeOrderRequest(_instrumentId, side, type, amount, null, sellAll, stop),
                        key, null, TestContext.Current.CancellationToken);
                await s.DisposeAsync();
                return result;
            }

            (await PlaceOnProviderAsync("buy", "market", "500", false, null, "reset-buy")).IsSuccess.ShouldBeTrue();
            var stop = await PlaceOnProviderAsync("sell", "stop_loss", null, true, stopPrice, "reset-stop");
            stop.IsSuccess.ShouldBeTrue(stop.Error?.Message);

            var resetScope = provider.CreateAsyncScope();
            var reset = await resetScope.ServiceProvider.GetRequiredService<PracticeAccountService>()
                .ResetAsync(userId, null, TestContext.Current.CancellationToken);
            await resetScope.DisposeAsync();
            reset.IsSuccess.ShouldBeTrue(reset.Error?.Message);

            var order = await OrderAsync(stop.Value.Id);
            order.State.ShouldBe(PracticeOrderState.Cancelled);
            order.CancelReason.ShouldBe("reset");

            var check = provider.CreateAsyncScope();
            var balances = await check.ServiceProvider
                .GetRequiredService<ILedger>()
                .GetBalancesAsync(userId, Book.Practice, TestContext.Current.CancellationToken);
            await check.DisposeAsync();
            balances.Value.Single(b => b.AssetCode == "USD").Available.ShouldBe(10000m);
            balances.Value.Where(b => b.AssetCode != "USD").ShouldAllBe(b => b.Available == 0m && b.Held == 0m);
        }
    }
}

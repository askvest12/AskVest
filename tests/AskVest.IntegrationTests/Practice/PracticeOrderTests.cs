using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Catalog.Import.Data;
using AskVest.Modules.Ledger.Configuration;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Ledger.Services;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Practice;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PracticeOrderTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provider = null!;
    private string _instrumentId = null!;
    private string _assetCode = null!;

    public async ValueTask InitializeAsync()
    {
        var suffix = CatalogImportTestData.Suffix();
        _instrumentId = $"instr-po-{suffix}";
        var symbol = $"PO{suffix}".ToUpperInvariant();
        _assetCode = $"P.{symbol}";
        _provider = PracticeTestServices.CreateProvider(
            fixture,
            _clock,
            marketDataSettings: new Dictionary<string, string?>
            {
                // A provider symbol containing UNKNOWN lets the unknown_symbol
                // scenario mark this instrument unmapped.
                [$"MarketData:SymbolOverrides:{_instrumentId}"] = $"UNKNOWN-{symbol}",
            });
        var instrument = CatalogImportTestData.Instrument(_instrumentId) with
        {
            Symbol = symbol,
            ListingExchange = "NMS",
        };
        var imported = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], [instrument], TestContext.Current.CancellationToken);
        imported.IsSuccess.ShouldBeTrue(imported.Error?.Message);
        var poller = _provider.GetRequiredService<PricePoller>();
        (await poller.RunCycleAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    private async Task<Guid> StartedUserAsync()
    {
        var userId = Guid.CreateVersion7();
        var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<PracticeAccountService>()
            .StartAsync(userId, null, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return userId;
    }

    private async Task<MarketDataPrice> PriceAsync()
    {
        var scope = _provider.CreateAsyncScope();
        var price = await scope.ServiceProvider
            .GetRequiredService<IMarketData>()
            .GetLatestPriceAsync(_instrumentId, TestContext.Current.CancellationToken);
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

    private async Task<List<Dictionary<string, object?>>> JournalLinesAsync(Guid orderId)
        => await Database.QueryAsync(
            fixture.OwnerConnectionString,
            "SELECT a.kind, a.owner_type, a.owner_id, e.asset_code, e.amount " +
            "FROM ledger.journal_entries e " +
            "JOIN ledger.journals j ON j.id = e.journal_id " +
            "JOIN ledger.accounts a ON a.id = e.account_id " +
            $"WHERE j.reference_type = 'practice_order' AND j.reference_id = '{orderId}' " +
            "ORDER BY j.reason_code, e.line_number",
            TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task Market_Buy_Fills_With_The_Spec_Journal_Lines()
    {
        var userId = await StartedUserAsync();
        var price = await PriceAsync();
        price.UsableForExecution.ShouldBeTrue($"label={price.Label} session={price.Session}");

        var result = await PlaceAsync(userId, "buy", amount: "1000");
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var order = result.Value;
        order.State.ShouldBe(PracticeOrderState.Filled);
        order.Side.ShouldBe(PracticeOrderSide.Buy);
        order.Type.ShouldBe(PracticeOrderType.Market);
        order.FilledQuantity.ShouldNotBeNull();
        order.FillPrice.ShouldBe(price.Price);
        order.Fee.ShouldBe(0m);
        order.FillJournalId.ShouldNotBeNull();

        var expected = PracticeOrderMath.ComputeBuy(1000m, price.Price!.Value, 6, 0);
        order.FilledQuantity.ShouldBe(expected.Quantity);
        order.CashAmount.ShouldBe(expected.Cost);

        var lines = await JournalLinesAsync(order.Id);
        lines.Count.ShouldBe(4);
        lines.ShouldContain(line =>
            (string)line["kind"]! == "user_available" && (string)line["asset_code"]! == "USD"
            && (decimal)line["amount"]! == -expected.Cost);
        lines.ShouldContain(line =>
            (string)line["kind"]! == "system_clearing" && (string)line["asset_code"]! == "USD"
            && (decimal)line["amount"]! == expected.Cost);
        lines.ShouldContain(line =>
            (string)line["kind"]! == "system_clearing" && (string)line["asset_code"]! == _assetCode
            && (decimal)line["amount"]! == -expected.Quantity);
        lines.ShouldContain(line =>
            (string)line["kind"]! == "user_available" && (string)line["asset_code"]! == _assetCode
            && (decimal)line["amount"]! == expected.Quantity);

        (await EventCountAsync(userId, "order_filled")).ShouldBe(1);
        (await OutboxCountAsync(order.Id)).ShouldBe(1);

        var scope = _provider.CreateAsyncScope();
        var balances = await scope.ServiceProvider
            .GetRequiredService<ILedger>()
            .GetBalancesAsync(userId, Book.Practice, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        balances.Value.Single(b => b.AssetCode == "USD").Available.ShouldBe(10000m - expected.Cost);
        balances.Value.Single(b => b.AssetCode == _assetCode).Available.ShouldBe(expected.Quantity);
    }

    [Fact]
    public async Task Buy_With_Fee_Posts_The_Fee_Leg_To_System_Fees()
    {
        await using var provider = PracticeTestServices.CreateProvider(
            fixture,
            _clock,
            practiceSettings: new Dictionary<string, string?> { ["Practice:FeeBasisPoints"] = "50" });
        var userId = Guid.CreateVersion7();
        var scope = provider.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<PracticeAccountService>()
            .StartAsync(userId, null, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        var result = await scope.ServiceProvider.GetRequiredService<PracticeOrderService>()
            .PlaceAsync(
                userId,
                new PracticeOrderRequest(_instrumentId, "buy", "market", "1000", null, null, null),
                "fee-key",
                null,
                TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Fee.GetValueOrDefault().ShouldBeGreaterThan(0m);

        var lines = await JournalLinesAsync(result.Value.Id);
        lines.ShouldContain(line =>
            (string)line["kind"]! == "system_fees" && (string)line["asset_code"]! == "USD"
            && (decimal)line["amount"]! == result.Value.Fee!.Value);
        lines.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Sell_By_Quantity_And_Sell_All_Fill_From_Available()
    {
        var userId = await StartedUserAsync();
        var buy = await PlaceAsync(userId, "buy", amount: "500");
        buy.IsSuccess.ShouldBeTrue();
        var quantity = buy.Value.FilledQuantity!.Value;

        var halfQuantity = PracticeRounding.RoundQuantity(quantity / 2m, 6);
        var half = await PlaceAsync(userId, "sell", quantity: (quantity / 2m).ToString());
        half.IsSuccess.ShouldBeTrue(half.Error?.Message);
        half.Value.State.ShouldBe(PracticeOrderState.Filled);
        half.Value.FilledQuantity.ShouldBe(halfQuantity);

        var rest = await PlaceAsync(userId, "sell", sellAll: true);
        rest.IsSuccess.ShouldBeTrue(rest.Error?.Message);
        rest.Value.SellAll.ShouldBeTrue();
        rest.Value.RequestedQuantity.ShouldBeNull();
        rest.Value.FilledQuantity.ShouldBe(quantity - halfQuantity);

        var scope = _provider.CreateAsyncScope();
        var balances = await scope.ServiceProvider
            .GetRequiredService<ILedger>()
            .GetBalancesAsync(userId, Book.Practice, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        balances.Value.FirstOrDefault(b => b.AssetCode == _assetCode)!.Available.ShouldBe(0m);
    }

    [Fact]
    public async Task Insufficient_Cash_Fails_And_Changes_Nothing()
    {
        var userId = await StartedUserAsync();
        var result = await PlaceAsync(userId, "buy", amount: "20000");
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("ledger.insufficient_funds");

        var scope = _provider.CreateAsyncScope();
        var count = await scope.ServiceProvider.GetRequiredService<PracticeDbContext>()
            .PracticeOrders.CountAsync(
                order => order.UserId == userId, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        count.ShouldBe(0);
    }

    [Fact]
    public async Task Oversell_And_Not_Started_Are_Rejected()
    {
        var userId = await StartedUserAsync();
        (await PlaceAsync(userId, "buy", amount: "100")).IsSuccess.ShouldBeTrue();

        var oversell = await PlaceAsync(userId, "sell", quantity: "999999");
        oversell.IsFailure.ShouldBeTrue();
        oversell.Error!.Code.ShouldBe(ErrorCodes.PracticeInsufficientPosition);

        var stranger = Guid.CreateVersion7();
        var notStarted = await PlaceAsync(stranger, "buy", amount: "10");
        notStarted.IsFailure.ShouldBeTrue();
        notStarted.Error!.Code.ShouldBe(ErrorCodes.PracticeAccountNotStarted);
    }

    [Fact]
    public async Task Unusable_Prices_Are_Rejected()
    {
        var userId = await StartedUserAsync();
        var fake = _provider.GetRequiredService<FakeMarketDataProvider>();
        var poller = _provider.GetRequiredService<PricePoller>();

        foreach (var scenario in new[] { "halted", "closed", "unknown_symbol" })
        {
            fake.SetScenario(scenario);
            await poller.RunCycleAsync(TestContext.Current.CancellationToken);
            var result = await PlaceAsync(userId, "buy", amount: "10", key: $"unusable-{scenario}");
            result.IsFailure.ShouldBeTrue($"scenario {scenario} should reject");
            result.Error!.Code.ShouldBe(ErrorCodes.PracticeMarketUnavailable);
            result.Error.Details.ShouldNotBeNull();
        }

        // A frozen clock plus an aged row yields the stale label.
        fake.SetScenario("normal");
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(400));
        var stale = await PlaceAsync(userId, "buy", amount: "10", key: "unusable-stale");
        stale.IsFailure.ShouldBeTrue();
        stale.Error!.Code.ShouldBe(ErrorCodes.PracticeMarketUnavailable);

        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        var recovered = await PlaceAsync(userId, "buy", amount: "10", key: "usable-again");
        recovered.IsSuccess.ShouldBeTrue(recovered.Error?.Message);
    }

    [Fact]
    public async Task Idempotency_Key_Replay_Returns_First_Order_Without_A_Second_Fill()
    {
        var userId = await StartedUserAsync();
        var first = await PlaceAsync(userId, "buy", amount: "100", key: "same-key");
        first.IsSuccess.ShouldBeTrue();
        var replay = await PlaceAsync(userId, "buy", amount: "100", key: "same-key");
        replay.IsSuccess.ShouldBeTrue();
        replay.Value.Id.ShouldBe(first.Value.Id);

        var scope = _provider.CreateAsyncScope();
        var count = await scope.ServiceProvider.GetRequiredService<PracticeDbContext>()
            .PracticeOrders.CountAsync(
                order => order.UserId == userId, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        count.ShouldBe(1);
        (await OutboxCountAsync(first.Value.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Fifty_Parallel_Buys_Of_100_Against_1000_Fill_Exactly_Ten()
    {
        await using var provider = PracticeTestServices.CreateProvider(
            fixture,
            _clock,
            practiceSettings: new Dictionary<string, string?> { ["Practice:StartingCredits"] = "1000" });
        var userId = Guid.CreateVersion7();
        var scope = provider.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<PracticeAccountService>()
            .StartAsync(userId, null, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        await scope.DisposeAsync();
        // Warm the lazy instrument-to-asset mapping so the burst exercises
        // order concurrency rather than the first-mapping race.
        var warmScope = provider.CreateAsyncScope();
        var warm = await warmScope.ServiceProvider.GetRequiredService<PracticeAssetResolver>()
            .ResolveAsync(_instrumentId, TestContext.Current.CancellationToken);
        await warmScope.DisposeAsync();
        warm.IsSuccess.ShouldBeTrue(warm.Error?.Message);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(i => PlaceOnAsync(provider, userId, _instrumentId, $"par-{i}")));

        results.Count(result => result.IsSuccess).ShouldBe(10);
        results.Count(result => result.IsFailure && result.Error!.Code == "ledger.insufficient_funds")
            .ShouldBe(40);

        var check = provider.CreateAsyncScope();
        var balances = await check.ServiceProvider
            .GetRequiredService<ILedger>()
            .GetBalancesAsync(userId, Book.Practice, TestContext.Current.CancellationToken);
        await check.DisposeAsync();
        // Each buy rounds its cost down, so a few ten-thousandths stay behind.
        var leftover = balances.Value.Single(b => b.AssetCode == "USD").Available;
        leftover.ShouldBeGreaterThanOrEqualTo(0m);
        leftover.ShouldBeLessThan(0.01m);
    }

    private static async Task<Result<PracticeOrder>> PlaceOnAsync(
        ServiceProvider provider,
        Guid userId,
        string instrumentId,
        string key)
    {
        var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<PracticeOrderService>()
            .PlaceAsync(
                userId,
                new PracticeOrderRequest(instrumentId, "buy", "market", "100", null, null, null),
                key,
                null,
                TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return result;
    }

    [Fact]
    public async Task Parallel_Buys_Of_The_Full_Position_Never_Oversell()
    {
        var userId = await StartedUserAsync();
        var buy = await PlaceAsync(userId, "buy", amount: "500");
        buy.IsSuccess.ShouldBeTrue();
        var quantity = buy.Value.FilledQuantity!.Value;

        var sells = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i =>
                PlaceAsync(userId, "sell", quantity: quantity.ToString(), key: $"sell-par-{i}")));
        sells.Count(result => result.IsSuccess).ShouldBe(1);
        sells.Count(result => result.IsFailure).ShouldBe(19);

        var scope = _provider.CreateAsyncScope();
        var balances = await scope.ServiceProvider
            .GetRequiredService<ILedger>()
            .GetBalancesAsync(userId, Book.Practice, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        balances.Value.Single(b => b.AssetCode == _assetCode).Available.ShouldBe(0m);
    }

    [Fact]
    public async Task Order_Reads_Are_Owned_Cursored_And_Filtered()
    {
        var userId = await StartedUserAsync();
        var other = await StartedUserAsync();
        for (var i = 0; i < 5; i++)
        {
            (await PlaceAsync(userId, "buy", amount: "10", key: $"list-{i}")).IsSuccess.ShouldBeTrue();
        }

        (await PlaceAsync(other, "buy", amount: "10", key: "other-order")).IsSuccess.ShouldBeTrue();

        var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<PracticeOrderService>();
        var page1 = await service.ListAsync(userId, null, 2, null, TestContext.Current.CancellationToken);
        var page2 = await service.ListAsync(userId, null, 2, page1.Value!.NextCursor, TestContext.Current.CancellationToken);
        var page3 = await service.ListAsync(userId, null, 2, page2.Value!.NextCursor, TestContext.Current.CancellationToken);
        var filtered = await service.ListAsync(userId, "filled", 10, null, TestContext.Current.CancellationToken);
        var missing = await service.GetAsync(userId, Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        var otherRows = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT id FROM practice.practice_orders WHERE user_id = '{other}' LIMIT 1",
            TestContext.Current.CancellationToken);
        var otherId = (Guid)otherRows[0]["id"]!;
        var stolen = await service.GetAsync(userId, otherId, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();

        page1.IsSuccess.ShouldBeTrue();
        page1.Value.Items.Count.ShouldBe(2);
        page1.Value.HasMore.ShouldBeTrue();
        var all = page1.Value.Items.Concat(page2.Value!.Items).Concat(page3.Value!.Items).ToList();
        all.Count.ShouldBe(5);
        all.Select(item => item.Id).Distinct().Count().ShouldBe(5);
        all.ShouldAllBe(item => item.State == "filled");
        filtered.Value!.Items.Count.ShouldBe(5);
        missing.IsFailure.ShouldBeTrue();
        missing.Error!.Code.ShouldBe(ErrorCodes.ResourceNotFound);
        stolen.IsFailure.ShouldBeTrue();
        stolen.Error!.Code.ShouldBe(ErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task Practice_Contract_Exposes_Account_And_Orders_Since()
    {
        var userId = await StartedUserAsync();
        (await PlaceAsync(userId, "buy", amount: "50", key: "contract-order")).IsSuccess.ShouldBeTrue();

        var scope = _provider.CreateAsyncScope();
        var contract = scope.ServiceProvider
            .GetRequiredService<AskVest.Modules.Practice.Contracts.IPractice>();
        var info = await contract.GetAccountInfoAsync(userId, TestContext.Current.CancellationToken);
        var orders = await contract.GetOrdersSinceAsync(
            userId, _clock.GetUtcNow().AddDays(-1), TestContext.Current.CancellationToken);
        var empty = await contract.GetOrdersSinceAsync(
            userId, _clock.GetUtcNow().AddDays(1), TestContext.Current.CancellationToken);
        await scope.DisposeAsync();

        info.Started.ShouldBeTrue();
        info.ResetCount.ShouldBe(0);
        orders.Count.ShouldBe(1);
        orders[0].OrderId.ShouldNotBe(Guid.Empty);
        orders[0].AssetCode.ShouldStartWith("P.");
        orders[0].Side.ShouldBe("buy");
        orders[0].State.ShouldBe("filled");
        empty.ShouldBeEmpty();
    }

    [Fact]
    public async Task Order_And_Ledger_Consistency_Holds_After_Mixed_Operations()
    {
        var userId = await StartedUserAsync();
        var first = await PlaceAsync(userId, "buy", amount: "200", key: "cons-1");
        var second = await PlaceAsync(userId, "buy", amount: "100", key: "cons-2");
        var sell = await PlaceAsync(userId, "sell", quantity: "1", key: "cons-3");
        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        sell.IsSuccess.ShouldBeTrue(sell.Error?.Message);

        var orphanJournals = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            "SELECT j.reference_id FROM ledger.journals j " +
            "WHERE j.book = 'practice' AND j.reference_type = 'practice_order' " +
            "AND j.reference_id NOT IN (SELECT id FROM practice.practice_orders)",
            TestContext.Current.CancellationToken);
        orphanJournals.ShouldBeEmpty();

        var fillsWithoutOrder = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            "SELECT count(*) AS n FROM ledger.journals j " +
            "WHERE j.book = 'practice' AND j.reason_code = 'trade_buy' " +
            "AND j.idempotency_key LIKE 'practice:order:%:fill' " +
            "AND j.reference_id NOT IN (SELECT id FROM practice.practice_orders)",
            TestContext.Current.CancellationToken);
        ((long)fillsWithoutOrder[0]["n"]!).ShouldBe(0);
    }

    [Fact]
    public async Task Reconciliation_Is_Clean_After_Orders()
    {
        var userId = await StartedUserAsync();
        (await PlaceAsync(userId, "buy", amount: "300", key: "rec-buy")).IsSuccess.ShouldBeTrue();
        (await PlaceAsync(userId, "sell", quantity: "0.5", key: "rec-sell")).IsSuccess.ShouldBeTrue();

        var logger = new ListLogger<LedgerReconciliationService>();
        var reconciliation = new LedgerReconciliationService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new LedgerOptions { ReconciliationIntervalMinutes = 1, ReconciliationPageSize = 500 }),
            _clock,
            logger);
        await reconciliation.RunCycleAsync(TestContext.Current.CancellationToken);
        logger.Messages.ShouldContain(
            m => m.Contains("zero mismatches"),
            customMessage: string.Join(" | ", logger.Messages));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add($"{logLevel}:{formatter(state, exception)}");
    }
}

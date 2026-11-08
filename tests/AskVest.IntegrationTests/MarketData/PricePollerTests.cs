using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.MarketData.Persistence;
using AskVest.Modules.MarketData.Persistence.Entities;
using AskVest.Modules.MarketData.Providers;
using AskVest.Modules.MarketData.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.MarketData;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PricePollerTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_Cycle_Populates_Every_Active_Instrument()
    {
        var ids = await ImportInstrumentsAsync(3, "pop");
        await using var provider = MarketDataTestServices.CreateProvider(fixture, new FakeTimeProvider(Start));
        var poller = provider.GetRequiredService<PricePoller>();
        var fake = provider.GetRequiredService<FakeMarketDataProvider>();

        var ran = await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        ran.ShouldBeTrue();
        fake.QuotesCallCount.ShouldBe(1);
        var rows = await LoadRowsAsync(provider, ids);
        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(r => r.Price > 0 && r.Session == "regular" && r.ProviderKey == "fake");
        rows.ShouldAllBe(r => r.AsOf == Start);
    }

    [Fact]
    public async Task Later_Cycles_Upsert_Without_Duplicates()
    {
        var ids = await ImportInstrumentsAsync(2, "ups");
        var clock = new FakeTimeProvider(Start);
        await using var provider = MarketDataTestServices.CreateProvider(fixture, clock);
        var poller = provider.GetRequiredService<PricePoller>();

        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await LoadRowsAsync(provider, ids);
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.AsOf == Start.AddMinutes(1));
    }

    [Fact]
    public async Task Failing_Cycle_Keeps_Old_Prices_And_Next_Good_Cycle_Recovers()
    {
        var ids = await ImportInstrumentsAsync(2, "out");
        var clock = new FakeTimeProvider(Start);
        await using var provider = MarketDataTestServices.CreateProvider(fixture, clock);
        var poller = provider.GetRequiredService<PricePoller>();
        var fake = provider.GetRequiredService<FakeMarketDataProvider>();

        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        var before = (await LoadRowsAsync(provider, ids))
            .ToDictionary(r => r.SubjectId, r => (r.Price, r.AsOf));

        fake.SetScenario("outage");
        clock.Advance(TimeSpan.FromMinutes(1));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        poller.ConsecutiveFailures.ShouldBe(1);
        var during = await LoadRowsAsync(provider, ids);
        during.ShouldAllBe(r => r.Price == before[r.SubjectId].Price && r.AsOf == before[r.SubjectId].AsOf);

        fake.SetScenario("normal");
        clock.Advance(TimeSpan.FromMinutes(1));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        poller.ConsecutiveFailures.ShouldBe(0);
        var after = await LoadRowsAsync(provider, ids);
        after.ShouldAllBe(r => r.AsOf == Start.AddMinutes(2));
    }

    [Fact]
    public async Task Rate_Limited_Answer_Waits_Retry_Delay_And_Retries_Once()
    {
        var ids = await ImportInstrumentsAsync(1, "rl");
        var clock = new FakeTimeProvider(Start);
        await using var provider = MarketDataTestServices.CreateProvider(
            fixture,
            clock,
            new Dictionary<string, string?> { ["MarketData:Fake:Scenario"] = "rate_limited" });
        var poller = provider.GetRequiredService<PricePoller>();
        var fake = provider.GetRequiredService<FakeMarketDataProvider>();

        var cycle = poller.RunCycleAsync(TestContext.Current.CancellationToken);
        while (!cycle.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await cycle;
        fake.QuotesCallCount.ShouldBe(2);
        poller.ConsecutiveFailures.ShouldBe(1);
        (await LoadRowsAsync(provider, ids)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Batches_Split_At_Configured_Size()
    {
        var ids = await ImportInstrumentsAsync(5, "bat");
        await using var provider = MarketDataTestServices.CreateProvider(
            fixture,
            new FakeTimeProvider(Start),
            new Dictionary<string, string?> { ["MarketData:BatchSize"] = "2" });
        var poller = provider.GetRequiredService<PricePoller>();
        var fake = provider.GetRequiredService<FakeMarketDataProvider>();

        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        fake.QuotesCallCount.ShouldBe(3);
        (await LoadRowsAsync(provider, ids)).Count.ShouldBe(5);
    }

    [Fact]
    public async Task Batch_Count_Never_Exceeds_Requests_Per_Minute_Budget()
    {
        var ids = await ImportInstrumentsAsync(5, "bud");
        await using var provider = MarketDataTestServices.CreateProvider(
            fixture,
            new FakeTimeProvider(Start),
            new Dictionary<string, string?>
            {
                ["MarketData:BatchSize"] = "2",
                ["MarketData:RequestsPerMinute"] = "1",
                ["MarketData:PollIntervalSeconds"] = "60",
            });
        var poller = provider.GetRequiredService<PricePoller>();
        var fake = provider.GetRequiredService<FakeMarketDataProvider>();

        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        fake.QuotesCallCount.ShouldBe(1);
        (await LoadRowsAsync(provider, ids)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Backoff_Grows_After_Five_Failures_And_Resets_On_Success()
    {
        var ids = await ImportInstrumentsAsync(1, "bof");
        var clock = new FakeTimeProvider(Start);
        await using var provider = MarketDataTestServices.CreateProvider(
            fixture,
            clock,
            new Dictionary<string, string?> { ["MarketData:Fake:Scenario"] = "outage" });
        var poller = provider.GetRequiredService<PricePoller>();
        var fake = provider.GetRequiredService<FakeMarketDataProvider>();

        for (var i = 0; i < 4; i++)
        {
            await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        }

        poller.NextDelay().ShouldBe(TimeSpan.FromSeconds(60));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        poller.NextDelay().ShouldBe(TimeSpan.FromSeconds(120));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        poller.NextDelay().ShouldBe(TimeSpan.FromSeconds(300));

        fake.SetScenario("normal");
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        poller.ConsecutiveFailures.ShouldBe(0);
        poller.NextDelay().ShouldBe(TimeSpan.FromSeconds(60));
        (await LoadRowsAsync(provider, ids)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_Symbols_Are_Marked_Unmapped_Logged_Once_Per_Hour_And_Do_Not_Stop_Others()
    {
        var suffix = CatalogImportTestData.Suffix();
        var known = CatalogImportTestData.Instrument($"instr-unk-k-{suffix}", null) with
        {
            Symbol = $"UNKG{suffix[..4]}",
            ListingExchange = "NMS",
        };
        var unknown = CatalogImportTestData.Instrument($"instr-unk-u-{suffix}", null) with
        {
            Symbol = $"UNKU{suffix[..4]}",
            ListingExchange = "NMS",
        };
        var result = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], [known, unknown], TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue(string.Join("; ", result.Error?.Details?.Select(d => d.Message) ?? []));

        var clock = new FakeTimeProvider(Start);
        await using var provider = MarketDataTestServices.CreateProvider(
            fixture,
            clock,
            new Dictionary<string, string?>
            {
                [$"MarketData:SymbolOverrides:{unknown.Id}"] = "NMS:UNKNOWN-MARKER",
            });
        var poller = provider.GetRequiredService<PricePoller>();
        var fake = provider.GetRequiredService<FakeMarketDataProvider>();

        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        var priced = await LoadRowsAsync(provider, [known.Id, unknown.Id]);
        priced.Count.ShouldBe(2);

        fake.SetScenario("unknown_symbol");
        clock.Advance(TimeSpan.FromMinutes(1));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        var rows = await LoadRowsAsync(provider, [known.Id, unknown.Id]);
        rows.Count.ShouldBe(2);
        var knownRow = rows.Single(r => r.SubjectId == known.Id);
        var unknownRow = rows.Single(r => r.SubjectId == unknown.Id);
        knownRow.UnmappedAt.ShouldBeNull();
        knownRow.AsOf.ShouldBe(Start.AddMinutes(1));
        unknownRow.UnmappedAt.ShouldBe(Start.AddMinutes(1));
        unknownRow.Price.ShouldBeGreaterThan(0);
        poller.UnmappedWarningsLogged.ShouldBe(1);

        clock.Advance(TimeSpan.FromMinutes(30));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        poller.UnmappedWarningsLogged.ShouldBe(1);

        clock.Advance(TimeSpan.FromHours(1));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        poller.UnmappedWarningsLogged.ShouldBe(2);
        fake.QuotesCallCount.ShouldBe(4);
    }

    [Fact]
    public async Task Backwards_Timestamps_Are_Ignored()
    {
        var ids = await ImportInstrumentsAsync(1, "bwd");
        var clock = new ReversibleClock(Start);
        await using var provider = MarketDataTestServices.CreateProvider(fixture, clock);
        var poller = provider.GetRequiredService<PricePoller>();

        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(2));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        var storedAsOf = (await LoadRowsAsync(provider, ids)).Single().AsOf;
        storedAsOf.ShouldBe(Start.AddHours(2));

        clock.Set(Start.AddHours(1));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = (await LoadRowsAsync(provider, ids)).Single();
        row.AsOf.ShouldBe(Start.AddHours(2));
    }

    [Fact]
    public async Task Outlier_Spike_Is_Held_Then_Confirmed_By_Repeated_Spike()
    {
        var ids = await ImportInstrumentsAsync(1, "spk");
        var clock = new FakeTimeProvider(Start);
        await using var provider = MarketDataTestServices.CreateProvider(fixture, clock);
        var poller = provider.GetRequiredService<PricePoller>();
        var fake = provider.GetRequiredService<FakeMarketDataProvider>();

        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        var normal = (await LoadRowsAsync(provider, ids)).Single();
        var normalPrice = normal.Price;

        fake.SetScenario("outlier_spike");
        clock.Advance(TimeSpan.FromMinutes(1));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        var held = (await LoadRowsAsync(provider, ids)).Single();
        held.Price.ShouldBe(normalPrice);
        held.SuspectPrice.ShouldNotBeNull();
        held.SuspectPrice!.Value.ShouldBeGreaterThan(normalPrice * 3m);

        clock.Advance(TimeSpan.FromMinutes(1));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        var confirmed = (await LoadRowsAsync(provider, ids)).Single();
        confirmed.Price.ShouldBe(held.SuspectPrice!.Value);
        confirmed.SuspectPrice.ShouldBeNull();
        confirmed.SuspectAt.ShouldBeNull();
    }

    [Fact]
    public async Task Outlier_Spike_Is_Discarded_When_Next_Tick_Returns_Near_Old_Price()
    {
        var ids = await ImportInstrumentsAsync(1, "dsc");
        var clock = new FakeTimeProvider(Start);
        await using var provider = MarketDataTestServices.CreateProvider(fixture, clock);
        var poller = provider.GetRequiredService<PricePoller>();
        var fake = provider.GetRequiredService<FakeMarketDataProvider>();

        await poller.RunCycleAsync(TestContext.Current.CancellationToken);
        var normal = (await LoadRowsAsync(provider, ids)).Single();

        fake.SetScenario("outlier_spike");
        clock.Advance(TimeSpan.FromMinutes(1));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        fake.SetScenario("normal");
        clock.Advance(TimeSpan.FromMinutes(1));
        await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        var row = (await LoadRowsAsync(provider, ids)).Single();
        row.SuspectPrice.ShouldBeNull();
        row.Price.ShouldBeLessThan(normal.Price * 2m);
    }

    [Fact]
    public async Task Second_Poller_Skips_When_Advisory_Lock_Is_Held()
    {
        await ImportInstrumentsAsync(2, "lck");
        var clock = new FakeTimeProvider(Start);
        await using var first = MarketDataTestServices.CreateProvider(fixture, clock);
        await using var second = MarketDataTestServices.CreateProvider(fixture, clock);
        var firstPoller = first.GetRequiredService<PricePoller>();
        var secondPoller = second.GetRequiredService<PricePoller>();
        var firstFake = first.GetRequiredService<FakeMarketDataProvider>();
        var secondFake = second.GetRequiredService<FakeMarketDataProvider>();

        var results = await Task.WhenAll(
            firstPoller.RunCycleAsync(TestContext.Current.CancellationToken),
            secondPoller.RunCycleAsync(TestContext.Current.CancellationToken));

        results.Count(r => r).ShouldBe(1);
        (firstFake.QuotesCallCount + secondFake.QuotesCallCount).ShouldBe(1);
    }

    [Fact]
    public async Task Poller_Skips_Cycle_While_Another_Session_Holds_The_Lock()
    {
        await ImportInstrumentsAsync(1, "loc");
        var clock = new FakeTimeProvider(Start);
        await using var provider = MarketDataTestServices.CreateProvider(fixture, clock);
        var poller = provider.GetRequiredService<PricePoller>();
        var fake = provider.GetRequiredService<FakeMarketDataProvider>();

        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection))
        {
            hold.Parameters.AddWithValue("key", PricePoller.AdvisoryLockKey);
            await hold.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var ran = await poller.RunCycleAsync(TestContext.Current.CancellationToken);

        ran.ShouldBeFalse();
        fake.QuotesCallCount.ShouldBe(0);

        await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
        release.Parameters.AddWithValue("key", PricePoller.AdvisoryLockKey);
        await release.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        (await poller.RunCycleAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        fake.QuotesCallCount.ShouldBe(1);
    }

    private async Task<List<string>> ImportInstrumentsAsync(int count, string prefix)
    {
        var suffix = CatalogImportTestData.Suffix();
        var instruments = Enumerable.Range(0, count)
            .Select(i => CatalogImportTestData.Instrument($"instr-{prefix}-{i}-{suffix}", null) with
            {
                Symbol = $"{prefix.ToUpperInvariant()}{i}{suffix[..4]}",
                ListingExchange = "NMS",
            })
            .ToList();
        var result = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], instruments, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue(string.Join("; ", result.Error?.Details?.Select(d => d.Message) ?? []));
        return instruments.Select(i => i.Id).ToList();
    }

    private sealed class ReversibleClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _utcNow = start;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Set(DateTimeOffset value) => _utcNow = value;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }

    private static async Task<List<LatestPrice>> LoadRowsAsync(
        ServiceProvider provider,
        IReadOnlyCollection<string> ids)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketDataDbContext>();
        return await db.LatestPrices
            .Where(p => ids.Contains(p.SubjectId))
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}

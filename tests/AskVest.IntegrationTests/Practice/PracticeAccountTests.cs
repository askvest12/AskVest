using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Ledger.Configuration;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Ledger.Services;
using AskVest.Modules.MarketData.Services;
using AskVest.Modules.Practice.Persistence;
using AskVest.Modules.Practice.Services;
using AskVest.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Practice;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PracticeAccountTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provider = null!;

    public ValueTask InitializeAsync()
    {
        _provider = PracticeTestServices.CreateProvider(fixture, _clock);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    private async Task<Result<AskVest.Modules.Practice.Persistence.Entities.PracticeAccount>> StartAsync(Guid userId)
    {
        var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<PracticeAccountService>()
            .StartAsync(userId, null, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return result;
    }

    private async Task<Result<PracticeAccountStatus>> GetStatusAsync(Guid userId)
    {
        var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<PracticeAccountService>()
            .GetStatusAsync(userId, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return result;
    }

    private async Task<Result<PracticeAccountStatus>> ResetAsync(Guid userId)
    {
        var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<PracticeAccountService>()
            .ResetAsync(userId, null, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return result;
    }

    private async Task<Result<LedgerJournal>> PostAsync(PostJournalRequest request)
    {
        var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<ILedger>()
            .PostJournalAsync(request, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return result;
    }

    private async Task<long> CountAsync(string sql)
        => await Database.ExecuteScalarAsync<long>(
            fixture.OwnerConnectionString, sql, TestContext.Current.CancellationToken);

    private static string UserFilter(Guid userId) => $"'{userId}'";

    [Fact]
    public async Task Start_Grants_Exactly_Once_Under_Twenty_Parallel_Calls()
    {
        var userId = Guid.CreateVersion7();
        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => StartAsync(userId)));

        results.ShouldAllBe(result => result.IsSuccess);
        (await CountAsync(
            $"SELECT count(*) FROM practice.practice_accounts WHERE user_id = {UserFilter(userId)}"))
            .ShouldBe(1);
        (await CountAsync(
            $"SELECT count(*) FROM ledger.journals WHERE book = 'practice' " +
            $"AND reason_code = 'practice_credit_grant' AND reference_id = {UserFilter(userId)}"))
            .ShouldBe(1);
        (await CountAsync(
            $"SELECT count(*) FROM practice.practice_events WHERE user_id = {UserFilter(userId)} " +
            "AND event_type = 'account_started'"))
            .ShouldBe(1);

        var scope = _provider.CreateAsyncScope();
        var balances = await scope.ServiceProvider
            .GetRequiredService<ILedger>()
            .GetBalancesAsync(userId, Book.Practice, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        balances.IsSuccess.ShouldBeTrue();
        balances.Value.Single(balance => balance.AssetCode == "USD").Available.ShouldBe(10000m);

        var logger = new ListLogger<LedgerReconciliationService>();
        var reconciliation = new LedgerReconciliationService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new LedgerOptions { ReconciliationIntervalMinutes = 1, ReconciliationPageSize = 500 }),
            TimeProvider.System,
            logger);
        await reconciliation.RunCycleAsync(TestContext.Current.CancellationToken);
        logger.Messages.ShouldContain(m => m.Contains("zero mismatches"),
            customMessage: string.Join(" | ", logger.Messages));
    }

    [Fact]
    public async Task Repeated_Start_Returns_The_Same_Account_Without_A_Second_Grant()
    {
        var userId = Guid.CreateVersion7();
        var first = await StartAsync(userId);
        first.IsSuccess.ShouldBeTrue();
        var second = await StartAsync(userId);
        second.IsSuccess.ShouldBeTrue();

        second.Value.StartedAt.ShouldBe(first.Value.StartedAt);
        (await CountAsync(
            $"SELECT count(*) FROM ledger.journals WHERE book = 'practice' " +
            $"AND reason_code = 'practice_credit_grant' AND reference_id = {UserFilter(userId)}"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Status_Reports_Not_Started_Then_Started_With_Cash()
    {
        var userId = Guid.CreateVersion7();
        var before = await GetStatusAsync(userId);
        before.IsSuccess.ShouldBeTrue();
        before.Value.Started.ShouldBeFalse();

        (await StartAsync(userId)).IsSuccess.ShouldBeTrue();
        var after = await GetStatusAsync(userId);
        after.IsSuccess.ShouldBeTrue();
        after.Value.Started.ShouldBeTrue();
        after.Value.Cash.ShouldBe(10000m);
        after.Value.ResetCount.ShouldBe(0);
        after.Value.ResetsLeft.ShouldBe(3);
        after.Value.NextResetAt.ShouldBe(_clock.GetUtcNow().AddHours(24));
    }

    [Fact]
    public async Task Reset_Refused_Inside_Cooldown_Then_Succeeds_After()
    {
        var userId = Guid.CreateVersion7();
        (await StartAsync(userId)).IsSuccess.ShouldBeTrue();

        _clock.Advance(TimeSpan.FromHours(23));
        var refused = await ResetAsync(userId);
        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCodes.PracticeResetNotAllowed);

        _clock.Advance(TimeSpan.FromHours(2));
        var allowed = await ResetAsync(userId);
        allowed.IsSuccess.ShouldBeTrue(allowed.Error?.Message);
        allowed.Value.ResetCount.ShouldBe(1);
        allowed.Value.Cash.ShouldBe(10000m);
        allowed.Value.ResetsLeft.ShouldBe(2);

        (await CountAsync(
            $"SELECT count(*) FROM practice.practice_events WHERE user_id = {UserFilter(userId)} " +
            "AND event_type = 'account_reset'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Reset_Refused_After_The_Rolling_Window_Limit()
    {
        var userId = Guid.CreateVersion7();
        var provider = PracticeTestServices.CreateProvider(
            fixture, _clock, new Dictionary<string, string?> { ["Practice:ResetCooldownHours"] = "0" });
        await using (provider)
        {
            async Task<Result<PracticeAccountStatus>> ResetHere()
            {
                var scope = provider.CreateAsyncScope();
                var result = await scope.ServiceProvider
                    .GetRequiredService<PracticeAccountService>()
                    .ResetAsync(userId, null, TestContext.Current.CancellationToken);
                await scope.DisposeAsync();
                return result;
            }

            async Task StartHere()
            {
                var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<PracticeAccountService>()
                    .StartAsync(userId, null, TestContext.Current.CancellationToken);
                await scope.DisposeAsync();
            }

            await StartHere();
            for (var i = 0; i < 3; i++)
            {
                (await ResetHere()).IsSuccess.ShouldBeTrue();
            }

            var fourth = await ResetHere();
            fourth.IsFailure.ShouldBeTrue();
            fourth.Error!.Code.ShouldBe(ErrorCodes.PracticeResetNotAllowed);
        }
    }

    [Fact]
    public async Task Reset_Sweeps_Cash_And_Practice_Assets_Back_To_System()
    {
        var userId = Guid.CreateVersion7();
        (await StartAsync(userId)).IsSuccess.ShouldBeTrue();

        // Simulate a spent cash position and a held practice asset.
        var assetCode = $"P.TST{Guid.CreateVersion7():N}".ToUpperInvariant()[..16];
        var scope = _provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        (await ledger.RegisterAssetAsync(assetCode, AssetKind.Token, 6, TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();
        (await ledger.PostJournalAsync(new PostJournalRequest(
            $"practice:test-credit-{Guid.CreateVersion7():N}",
            Book.Practice,
            LedgerReasonCodes.Deposit,
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Practice, userId), assetCode, 12.5m),
                new LedgerLine(LedgerAccounts.SystemClearing(Book.Practice), assetCode, -12.5m),
            ]), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await ledger.PostJournalAsync(new PostJournalRequest(
            $"practice:test-spend-{Guid.CreateVersion7():N}",
            Book.Practice,
            LedgerReasonCodes.Withdrawal,
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Practice, userId), "USD", -400m),
                new LedgerLine(LedgerAccounts.SystemClearing(Book.Practice), "USD", 400m),
            ]), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        await scope.DisposeAsync();

        _clock.Advance(TimeSpan.FromHours(25));
        var reset = await ResetAsync(userId);
        reset.IsSuccess.ShouldBeTrue(reset.Error?.Message);

        scope = _provider.CreateAsyncScope();
        var balances = await scope.ServiceProvider
            .GetRequiredService<ILedger>()
            .GetBalancesAsync(userId, Book.Practice, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        balances.IsSuccess.ShouldBeTrue();
        balances.Value.Single(balance => balance.AssetCode == "USD").Available.ShouldBe(10000m);
        balances.Value
            .Where(balance => balance.AssetCode == assetCode)
            .ShouldAllBe(balance => balance.Available == 0m && balance.Held == 0m);
    }

    [Fact]
    public async Task Positions_Show_Cash_Only_Before_Any_Holding()
    {
        var userId = Guid.CreateVersion7();
        (await StartAsync(userId)).IsSuccess.ShouldBeTrue();

        var scope = _provider.CreateAsyncScope();
        var positions = await scope.ServiceProvider
            .GetRequiredService<PracticePositionService>()
            .GetAsync(userId, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        positions.IsSuccess.ShouldBeTrue();
        positions.Value.Cash.ShouldBe(10000m);
        positions.Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Positions_Map_Balances_To_Instruments_With_Prices()
    {
        var suffix = CatalogImportTestData.Suffix();
        var instrument = CatalogImportTestData.Instrument($"instr-ps-{suffix}", null) with
        {
            Symbol = $"PS{suffix[..4]}",
            ListingExchange = "NMS",
        };
        var imported = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], [instrument], TestContext.Current.CancellationToken);
        imported.IsSuccess.ShouldBeTrue(imported.Error?.Message);

        var poller = _provider.GetRequiredService<PricePoller>();
        (await poller.RunCycleAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        var userId = Guid.CreateVersion7();
        (await StartAsync(userId)).IsSuccess.ShouldBeTrue();
        var assetCode = $"P.{instrument.Symbol}".ToUpperInvariant();

        var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();
        db.PracticeInstruments.Add(new AskVest.Modules.Practice.Persistence.Entities.PracticeInstrument
        {
            InstrumentId = instrument.Id,
            AssetCode = assetCode,
            CreatedAt = _clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        (await ledger.RegisterAssetAsync(assetCode, AssetKind.Token, 6, TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();
        (await ledger.PostJournalAsync(new PostJournalRequest(
            $"practice:test-pos-{Guid.CreateVersion7():N}",
            Book.Practice,
            LedgerReasonCodes.Deposit,
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Practice, userId), assetCode, 3m),
                new LedgerLine(LedgerAccounts.SystemClearing(Book.Practice), assetCode, -3m),
            ]), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await ledger.PostJournalAsync(new PostJournalRequest(
            $"practice:test-pos-hold-{Guid.CreateVersion7():N}",
            Book.Practice,
            LedgerReasonCodes.Hold,
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Practice, userId), assetCode, -1m),
                new LedgerLine(LedgerAccounts.UserHeld(Book.Practice, userId), assetCode, 1m),
            ]), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        var positions = await scope.ServiceProvider
            .GetRequiredService<PracticePositionService>()
            .GetAsync(userId, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();

        positions.IsSuccess.ShouldBeTrue(positions.Error?.Message);
        var item = positions.Value.Items.ShouldHaveSingleItem();
        item.InstrumentId.ShouldBe(instrument.Id);
        item.Symbol.ShouldBe(instrument.Symbol);
        item.AvailableQuantity.ShouldBe(2m);
        item.HeldQuantity.ShouldBe(1m);
        item.Price.ShouldNotBeNull();
        item.PriceLabel.ShouldNotBeNull();
        item.MarketValue.ShouldNotBeNull();
    }

    [Fact]
    public async Task Practice_Never_Touches_The_Real_Book()
    {
        var userId = Guid.CreateVersion7();
        (await StartAsync(userId)).IsSuccess.ShouldBeTrue();
        _clock.Advance(TimeSpan.FromHours(25));
        (await ResetAsync(userId)).IsSuccess.ShouldBeTrue();

        var realEntries = await CountAsync(
            "SELECT count(*) FROM ledger.journal_entries e " +
            "JOIN ledger.accounts a ON a.id = e.account_id " +
            $"WHERE a.book = 'real' AND a.owner_id = {UserFilter(userId)}");
        realEntries.ShouldBe(0);

        var practiceEntries = await CountAsync(
            "SELECT count(*) FROM ledger.journal_entries e " +
            "JOIN ledger.accounts a ON a.id = e.account_id " +
            $"WHERE a.book = 'practice' AND a.owner_id = {UserFilter(userId)}");
        practiceEntries.ShouldBeGreaterThan(0);
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

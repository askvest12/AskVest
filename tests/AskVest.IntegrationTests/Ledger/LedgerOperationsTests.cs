using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Ledger;
using AskVest.Modules.Ledger.Configuration;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Ledger.Persistence;
using AskVest.Modules.Ledger.Services;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Ledger;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LedgerOperationsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ServiceProvider _provider = CreateProvider(fixture);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Deletion_Blocker_Reports_Real_Balance_Remaining_Only_When_Non_Zero()
    {
        var userId = Guid.CreateVersion7();
        var blocker = await NewBlockerAsync();

        (await blocker.GetBlockReasonsAsync(userId, TestContext.Current.CancellationToken)).ShouldBeEmpty();

        await PostAsync(CreditRequest(userId, Book.Real, 10m, $"blk-{Guid.CreateVersion7():N}"));
        (await blocker.GetBlockReasonsAsync(userId, TestContext.Current.CancellationToken))
            .ShouldBe(["real_balance_remaining"]);

        await PostAsync(DebitRequest(userId, Book.Real, 10m, $"blkd-{Guid.CreateVersion7():N}"));
        (await blocker.GetBlockReasonsAsync(userId, TestContext.Current.CancellationToken)).ShouldBeEmpty();

        await PostAsync(CreditRequest(userId, Book.Practice, 10m, $"blkp-{Guid.CreateVersion7():N}"));
        (await blocker.GetBlockReasonsAsync(userId, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconciliation_Reports_Zero_Mismatches_After_Mixed_Operations()
    {
        var userId = Guid.CreateVersion7();
        await PostAsync(CreditRequest(userId, Book.Practice, 100m, $"rec-p-{Guid.CreateVersion7():N}"));
        await PostAsync(CreditRequest(userId, Book.Real, 50m, $"rec-r-{Guid.CreateVersion7():N}"));
        await PostAsync(DebitRequest(userId, Book.Real, 20m, $"rec-d-{Guid.CreateVersion7():N}"));

        var logger = new ListLogger<LedgerReconciliationService>();
        var service = NewReconciliation(logger);
        await service.RunCycleAsync(TestContext.Current.CancellationToken);

        logger.Messages.ShouldContain(m => m.Contains("zero mismatches"));
        logger.Messages.ShouldNotContain(m => m.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconciliation_Detects_A_Corrupted_Balance_And_Non_Netting_Book()
    {
        var userId = Guid.CreateVersion7();
        var journal = await PostAsync(CreditRequest(userId, Book.Real, 30m, $"cor-{Guid.CreateVersion7():N}"));
        journal.IsSuccess.ShouldBeTrue();

        var accountId = await ScalarAsync<Guid>(
            "SELECT b.account_id FROM ledger.account_balances b " +
            "JOIN ledger.accounts a ON a.id = b.account_id " +
            "WHERE a.owner_id = '" + userId + "' AND a.book = 'real'");
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"UPDATE ledger.account_balances SET balance = balance + 5 WHERE account_id = '{accountId}'",
            TestContext.Current.CancellationToken);

        var logger = new ListLogger<LedgerReconciliationService>();
        var service = NewReconciliation(logger);
        await service.RunCycleAsync(TestContext.Current.CancellationToken);

        logger.Messages.ShouldContain(m => m.Contains("mismatch") && m.Contains(accountId.ToString()));
        logger.Messages.ShouldContain(m => m.Contains("non-zero net"));

        var balance = await ScalarAsync<decimal>(
            $"SELECT balance FROM ledger.account_balances WHERE account_id = '{accountId}'");
        balance.ShouldBe(35m);
    }

    [Fact]
    public async Task Reconciliation_Keeps_Running_After_A_Database_Error()
    {
        var badConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddPlatformPersistence(badConfiguration);
        services.AddLedgerModule(badConfiguration);
        await using var badProvider = services.BuildServiceProvider();

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = new ListLogger<LedgerReconciliationService>();
        var service = new LedgerReconciliationService(
            badProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new LedgerOptions { ReconciliationIntervalMinutes = 1, ReconciliationPageSize = 10 }),
            clock,
            logger);

        await service.StartAsync(CancellationToken.None);

        var firstDeadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (!logger.Messages.Any(m => m.Contains("cycle failed"))
            && TimeProvider.System.GetUtcNow() < firstDeadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        logger.Messages.ShouldContain(m => m.Contains("cycle failed"));

        await Task.Delay(300, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(2));
        var secondDeadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (logger.Messages.Count(m => m.Contains("cycle failed")) < 2
            && TimeProvider.System.GetUtcNow() < secondDeadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        logger.Messages.Count(m => m.Contains("cycle failed")).ShouldBeGreaterThanOrEqualTo(2);
        await service.StopAsync(CancellationToken.None);
    }

    private LedgerReconciliationService NewReconciliation(ListLogger<LedgerReconciliationService> logger)
        => new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new LedgerOptions { ReconciliationIntervalMinutes = 1, ReconciliationPageSize = 500 }),
            TimeProvider.System,
            logger);

    private async Task<IDeletionBlocker> NewBlockerAsync()
    {
        var scope = _provider.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<IDeletionBlocker>();
    }

    private async Task<Result<LedgerJournal>> PostAsync(PostJournalRequest request)
    {
        var scope = _provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var result = await ledger.PostJournalAsync(request, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return result;
    }

    private static ServiceProvider CreateProvider(PostgresFixture fixture)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = fixture.AppConnectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddPlatformPersistence(configuration);
        services.AddLedgerModule(configuration);
        return services.BuildServiceProvider();
    }

    private static PostJournalRequest CreditRequest(Guid userId, Book book, decimal amount, string key)
        => new(
            key,
            book,
            LedgerReasonCodes.Deposit,
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(book, userId), "USD", amount),
                new LedgerLine(
                    book == Book.Practice ? LedgerAccounts.PracticeIssuance(book) : LedgerAccounts.SystemClearing(book),
                    "USD",
                    -amount),
            ]);

    private static PostJournalRequest DebitRequest(Guid userId, Book book, decimal amount, string key)
        => new(
            key,
            book,
            LedgerReasonCodes.Withdrawal,
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(book, userId), "USD", -amount),
                new LedgerLine(
                    book == Book.Practice ? LedgerAccounts.PracticeIssuance(book) : LedgerAccounts.SystemClearing(book),
                    "USD",
                    amount),
            ]);

    private async Task<T> ScalarAsync<T>(string sql)
    {
        var rows = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            sql,
            TestContext.Current.CancellationToken);
        return (T)rows[0].Values.First()!;
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

using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Ledger;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Ledger.Persistence;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Ledger;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LedgerPostingTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ServiceProvider _provider = CreateProvider(fixture);

    [Fact]
    public async Task Balanced_Journal_Updates_Balances_Entries_And_Enqueues_Once()
    {
        var userId = Guid.CreateVersion7();
        var journal = await PostAsync(CreditRequest(userId, Book.Practice, 100m, "bal-1"));

        journal.IsSuccess.ShouldBeTrue();
        var balances = await GetBalancesAsync(userId, Book.Practice);
        balances.ShouldBe([new LedgerBalance("USD", 100m, 0m)]);

        var entries = await CountAsync("ledger.journal_entries", $"journal_id = '{journal.Value.Id}'");
        entries.ShouldBe(2L);
        (await CountAsync(
            "platform.outbox_messages",
            $"type = 'LedgerJournalPosted' AND dedupe_key = 'ledger:journal-posted:{journal.Value.Id}'")).ShouldBe(1L);
    }

    [Fact]
    public async Task Unbalanced_Journal_Fails_And_Writes_Nothing()
    {
        var userId = Guid.CreateVersion7();
        var request = new PostJournalRequest(
            "unbal-1",
            Book.Practice,
            LedgerReasonCodes.Deposit,
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Practice, userId), "USD", 10m),
                new LedgerLine(LedgerAccounts.PracticeIssuance(Book.Practice), "USD", -4m),
            ]);

        var result = await PostAsync(request);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCodes.LedgerUnbalanced);
        (await CountAsync("ledger.journals", "idempotency_key = 'unbal-1'")).ShouldBe(0L);
        (await ScalarAsync(
            "SELECT coalesce(sum(balance), 0) FROM ledger.account_balances b " +
            "JOIN ledger.accounts a ON a.id = b.account_id WHERE a.owner_id = '" + userId + "'")).ShouldBe(0m);
    }

    [Fact]
    public async Task Unknown_Asset_Fails_With_Asset_Unknown()
    {
        var userId = Guid.CreateVersion7();
        var request = new PostJournalRequest(
            "asset-1",
            Book.Practice,
            LedgerReasonCodes.Deposit,
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Practice, userId), "NOPE", 10m),
                new LedgerLine(LedgerAccounts.PracticeIssuance(Book.Practice), "NOPE", -10m),
            ]);

        var result = await PostAsync(request);

        result.Error!.Code.ShouldBe(ErrorCodes.LedgerAssetUnknown);
        (await CountAsync("ledger.journals", "idempotency_key = 'asset-1'")).ShouldBe(0L);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_000_000_000)]
    [InlineData(1.0000001)]
    public async Task Invalid_Amounts_Fail_With_Amount_Invalid(double amount)
    {
        var userId = Guid.CreateVersion7();
        var decimalAmount = (decimal)amount;
        var request = new PostJournalRequest(
            $"amt-{Guid.CreateVersion7():N}",
            Book.Practice,
            LedgerReasonCodes.Deposit,
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Practice, userId), "USD", decimalAmount),
                new LedgerLine(LedgerAccounts.PracticeIssuance(Book.Practice), "USD", -decimalAmount),
            ]);

        var result = await PostAsync(request);

        result.Error!.Code.ShouldBe(ErrorCodes.LedgerAmountInvalid);
    }

    [Fact]
    public async Task Cross_Book_Lines_Fail_With_Book_Mismatch()
    {
        var userId = Guid.CreateVersion7();
        var request = new PostJournalRequest(
            "bookmix-1",
            Book.Real,
            LedgerReasonCodes.Deposit,
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Practice, userId), "USD", 10m),
                new LedgerLine(LedgerAccounts.SystemClearing(Book.Real), "USD", -10m),
            ]);

        var result = await PostAsync(request);

        result.Error!.Code.ShouldBe(ErrorCodes.LedgerBookMismatch);
    }

    [Fact]
    public async Task Same_Key_Same_Content_Replays_The_Original_Journal()
    {
        var userId = Guid.CreateVersion7();
        var request = CreditRequest(userId, Book.Practice, 25m, "idem-1");

        var first = await PostAsync(request);
        var second = await PostAsync(request);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        second.Value.Id.ShouldBe(first.Value.Id);
        (await CountAsync("ledger.journals", "idempotency_key = 'idem-1'")).ShouldBe(1L);
        (await CountAsync("ledger.journal_entries", $"journal_id = '{first.Value.Id}'")).ShouldBe(2L);
        var balances = await GetBalancesAsync(userId, Book.Practice);
        balances.ShouldBe([new LedgerBalance("USD", 25m, 0m)]);
    }

    [Fact]
    public async Task Same_Key_Different_Content_Returns_Idempotency_Conflict()
    {
        var userId = Guid.CreateVersion7();
        await PostAsync(CreditRequest(userId, Book.Practice, 25m, "idem-2"));

        var conflict = await PostAsync(CreditRequest(userId, Book.Practice, 30m, "idem-2"));

        conflict.IsFailure.ShouldBeTrue();
        conflict.Error!.Code.ShouldBe(ErrorCodes.LedgerIdempotencyConflict);
        var balances = await GetBalancesAsync(userId, Book.Practice);
        balances.ShouldBe([new LedgerBalance("USD", 25m, 0m)]);
    }

    [Fact]
    public async Task Fifty_Parallel_Identical_Posts_Create_One_Journal()
    {
        var userId = Guid.CreateVersion7();
        var request = CreditRequest(userId, Book.Practice, 1m, $"par-{Guid.CreateVersion7():N}");

        var results = await PostParallelAsync(_provider, _ => request, 50);

        results.ShouldAllBe(result => result.IsSuccess);
        results.Select(result => result.Value.Id).Distinct().Count().ShouldBe(1);
        (await CountAsync("ledger.journals", $"idempotency_key = '{request.IdempotencyKey}'")).ShouldBe(1L);
        var balances = await GetBalancesAsync(userId, Book.Practice);
        balances.ShouldBe([new LedgerBalance("USD", 1m, 0m)]);
    }

    [Fact]
    public async Task Insufficient_Funds_Fails_With_No_Partial_Effects()
    {
        var userId = Guid.CreateVersion7();
        await PostAsync(CreditRequest(userId, Book.Real, 100m, $"fund-{Guid.CreateVersion7():N}"));

        var debit = DebitRequest(userId, Book.Real, 150m, $"debit-{Guid.CreateVersion7():N}");
        var result = await PostAsync(debit);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCodes.LedgerInsufficientFunds);
        (await CountAsync("ledger.journals", $"idempotency_key = '{debit.IdempotencyKey}'")).ShouldBe(0L);
        (await CountAsync(
            "platform.outbox_messages",
            "payload::text LIKE '%" + userId + "%' AND type = 'LedgerJournalPosted'")).ShouldBe(1L);
        var balances = await GetBalancesAsync(userId, Book.Real);
        balances.ShouldBe([new LedgerBalance("USD", 100m, 0m)]);
    }

    [Fact]
    public async Task Hundred_Parallel_Debits_Of_Ten_Against_500_Succeed_Exactly_Fifty_Times()
    {
        var userId = Guid.CreateVersion7();
        await PostAsync(CreditRequest(userId, Book.Real, 500m, $"pool-{Guid.CreateVersion7():N}"));

        var results = await PostParallelAsync(
            _provider,
            i => DebitRequest(userId, Book.Real, 10m, $"drain-{Guid.CreateVersion7():N}-{i}"),
            100);

        results.Count(result => result.IsSuccess).ShouldBe(50);
        results.Count(result => result.IsFailure && result.Error!.Code == ErrorCodes.LedgerInsufficientFunds).ShouldBe(50);
        var balances = await GetBalancesAsync(userId, Book.Real);
        balances.ShouldBe([new LedgerBalance("USD", 0m, 0m)]);
    }

    [Fact]
    public async Task Hundred_Parallel_Opposing_Transfers_Conserve_The_Total()
    {
        var userA = Guid.CreateVersion7();
        var userB = Guid.CreateVersion7();
        await PostAsync(CreditRequest(userA, Book.Practice, 1000m, $"ta-{Guid.CreateVersion7():N}"));
        await PostAsync(CreditRequest(userB, Book.Practice, 1000m, $"tb-{Guid.CreateVersion7():N}"));

        var results = await PostParallelAsync(
            _provider,
            i => i % 2 == 0
                ? TransferRequest(userA, userB, Book.Practice, 1m, $"xfer-{Guid.CreateVersion7():N}-{i}")
                : TransferRequest(userB, userA, Book.Practice, 1m, $"xfer-{Guid.CreateVersion7():N}-{i}"),
            100);

        results.ShouldAllBe(result => result.IsSuccess);
        var balanceA = await GetBalancesAsync(userA, Book.Practice);
        var balanceB = await GetBalancesAsync(userB, Book.Practice);
        balanceA.ShouldBe([new LedgerBalance("USD", 1000m, 0m)]);
        balanceB.ShouldBe([new LedgerBalance("USD", 1000m, 0m)]);
    }

    [Fact]
    public async Task Reversal_Mirrors_The_Journal_And_Can_Happen_Only_Once()
    {
        var userId = Guid.CreateVersion7();
        var credit = await PostAsync(CreditRequest(userId, Book.Practice, 40m, $"rev-{Guid.CreateVersion7():N}"));
        credit.IsSuccess.ShouldBeTrue();

        var reversal = await ReverseAsync($"rev-back-{Guid.CreateVersion7():N}", credit.Value.Id);
        reversal.IsSuccess.ShouldBeTrue();

        var linked = await ScalarAsync(
            "SELECT count(*) FROM ledger.journals WHERE id = '" + reversal.Value.Id + "' " +
            "AND reverses_journal_id = '" + credit.Value.Id + "'");
        linked.ShouldBe(1m);
        var balances = await GetBalancesAsync(userId, Book.Practice);
        balances.ShouldBe([new LedgerBalance("USD", 0m, 0m)]);

        var entries = await CountAsync("ledger.journal_entries", $"journal_id = '{reversal.Value.Id}'");
        entries.ShouldBe(2L);
        var mirrored = await ScalarAsync(
            $"SELECT amount FROM ledger.journal_entries WHERE journal_id = '{reversal.Value.Id}' " +
            $"AND account_id IN (SELECT id FROM ledger.accounts WHERE owner_id = '{userId}')");
        mirrored.ShouldBe(-40m);

        var second = await ReverseAsync($"rev-back2-{Guid.CreateVersion7():N}", credit.Value.Id);
        second.IsFailure.ShouldBeTrue();
        second.Error!.Code.ShouldBe(ErrorCodes.LedgerAlreadyReversed);
    }

    [Fact]
    public async Task Reversal_That_Would_Overdraw_Fails_With_Insufficient_Funds()
    {
        var userId = Guid.CreateVersion7();
        var credit = await PostAsync(CreditRequest(userId, Book.Real, 100m, $"rover-{Guid.CreateVersion7():N}"));
        var debit = await PostAsync(DebitRequest(userId, Book.Real, 100m, $"ru-{Guid.CreateVersion7():N}"));
        debit.IsSuccess.ShouldBeTrue();

        var reversal = await ReverseAsync($"rback-{Guid.CreateVersion7():N}", credit.Value.Id);

        reversal.IsFailure.ShouldBeTrue();
        reversal.Error!.Code.ShouldBe(ErrorCodes.LedgerInsufficientFunds);
        var balances = await GetBalancesAsync(userId, Book.Real);
        balances.ShouldBe([new LedgerBalance("USD", 0m, 0m)]);
    }

    [Fact]
    public async Task Practice_And_Real_Books_Stay_Separate()
    {
        var userId = Guid.CreateVersion7();
        await PostAsync(CreditRequest(userId, Book.Practice, 100m, $"sep-p-{Guid.CreateVersion7():N}"));
        await PostAsync(CreditRequest(userId, Book.Real, 50m, $"sep-r-{Guid.CreateVersion7():N}"));

        var practice = await GetBalancesAsync(userId, Book.Practice);
        var real = await GetBalancesAsync(userId, Book.Real);
        practice.ShouldBe([new LedgerBalance("USD", 100m, 0m)]);
        real.ShouldBe([new LedgerBalance("USD", 50m, 0m)]);

        var practiceAccountCount = await ScalarAsync(
            "SELECT count(*) FROM ledger.accounts WHERE owner_id = '" + userId + "' AND book = 'practice'");
        var realAccountCount = await ScalarAsync(
            "SELECT count(*) FROM ledger.accounts WHERE owner_id = '" + userId + "' AND book = 'real'");
        practiceAccountCount.ShouldBe(1m);
        realAccountCount.ShouldBe(1m);
    }

    [Fact]
    public async Task Twenty_Parallel_First_Uses_Create_One_User_Account()
    {
        var userId = Guid.CreateVersion7();

        var results = await PostParallelAsync(
            _provider,
            i => CreditRequest(userId, Book.Practice, 1m, $"lazy-{Guid.CreateVersion7():N}-{i}"),
            20);

        results.ShouldAllBe(result => result.IsSuccess);
        var accountCount = await ScalarAsync(
            "SELECT count(*) FROM ledger.accounts WHERE owner_id = '" + userId + "' AND owner_type = 'user'");
        accountCount.ShouldBe(1m);
        var balances = await GetBalancesAsync(userId, Book.Practice);
        balances.ShouldBe([new LedgerBalance("USD", 20m, 0m)]);
    }

    [Fact]
    public async Task RegisterAsset_Is_Idempotent()
    {
        var ledger = await NewLedgerAsync();

        var first = await ledger.RegisterAssetAsync("TKN", AssetKind.Token, 8, TestContext.Current.CancellationToken);
        var second = await ledger.RegisterAssetAsync("tkn", AssetKind.Token, 8, TestContext.Current.CancellationToken);
        var conflict = await ledger.RegisterAssetAsync("TKN", AssetKind.Token, 6, TestContext.Current.CancellationToken);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        conflict.IsFailure.ShouldBeTrue();
        conflict.Error!.Code.ShouldBe(ErrorCodes.ResourceConflict);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
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

    private async Task<ILedger> NewLedgerAsync()
    {
        var scope = _provider.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<ILedger>();
    }

    private async Task<Result<LedgerJournal>> PostAsync(PostJournalRequest request)
    {
        var scope = _provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var result = await ledger.PostJournalAsync(request, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return result;
    }

    private async Task<Result<LedgerJournal>> ReverseAsync(string key, Guid journalId)
    {
        var scope = _provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var result = await ledger.ReverseJournalAsync(key, journalId, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return result;
    }

    private async Task<IReadOnlyList<LedgerBalance>> GetBalancesAsync(Guid userId, Book book)
    {
        var scope = _provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var result = await ledger.GetBalancesAsync(userId, book, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }

    private static async Task<IReadOnlyList<Result<LedgerJournal>>> PostParallelAsync(
        ServiceProvider provider,
        Func<int, PostJournalRequest> factory,
        int count)
    {
        var tasks = Enumerable.Range(0, count).Select(async index =>
        {
            await using var scope = provider.CreateAsyncScope();
            var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
            return await ledger.PostJournalAsync(factory(index), TestContext.Current.CancellationToken);
        });
        return await Task.WhenAll(tasks);
    }

    private static PostJournalRequest CreditRequest(Guid userId, Book book, decimal amount, string key)
    {
        var source = book == Book.Practice
            ? LedgerAccounts.PracticeIssuance(book)
            : LedgerAccounts.SystemClearing(book);
        return new PostJournalRequest(
            key,
            book,
            book == Book.Practice ? LedgerReasonCodes.PracticeCreditGrant : LedgerReasonCodes.Deposit,
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(book, userId), "USD", amount),
                new LedgerLine(source, "USD", -amount),
            ]);
    }

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

    private static PostJournalRequest TransferRequest(Guid from, Guid to, Book book, decimal amount, string key)
        => new(
            key,
            book,
            "adjustment",
            "test",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(book, from), "USD", -amount),
                new LedgerLine(LedgerAccounts.UserAvailable(book, to), "USD", amount),
            ]);

    private async Task<long> CountAsync(string table, string where)
    {
        var rows = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT count(*) AS n FROM {table} WHERE {where}",
            TestContext.Current.CancellationToken);
        return Convert.ToInt64(rows[0]["n"]);
    }

    private async Task<decimal> ScalarAsync(string sql)
    {
        var rows = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            sql,
            TestContext.Current.CancellationToken);
        return Convert.ToDecimal(rows[0].Values.First());
    }
}

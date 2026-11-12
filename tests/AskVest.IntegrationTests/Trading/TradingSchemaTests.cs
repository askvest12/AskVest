using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Trading.Persistence;
using AskVest.Platform;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Trading;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class TradingSchemaTests
{
    private readonly PostgresFixture _fixture;

    public TradingSchemaTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migrations_Reapply_As_NoOp()
    {
        var applied = await MigrationRunner.RunAsync(
            _fixture.OwnerConnectionString,
            [connectionString => new TradingDbContext(TradingDbContextOptions.Create(connectionString))],
            TestContext.Current.CancellationToken);
        applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Quote_Id_On_Orders_Is_Unique()
    {
        var quoteId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await InsertOrderAsync(Guid.CreateVersion7(), userId, "idem-a", quoteId: quoteId);
        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertOrderAsync(Guid.CreateVersion7(), userId, "idem-b", quoteId: quoteId));
        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task User_And_Idempotency_Key_On_Orders_Is_Unique()
    {
        var userId = Guid.CreateVersion7();
        await InsertOrderAsync(Guid.CreateVersion7(), userId, "idem-same");
        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertOrderAsync(Guid.CreateVersion7(), userId, "idem-same"));
        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task AppRole_CanReadWriteQuotesOrdersAndEvents()
    {
        var quoteId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $$"""
            INSERT INTO trading.quotes
                (id, user_id, token_id, side, quantity, price, gross_amount, our_fee,
                 gateway_fee, total, settlement_asset, gateway_key, gateway_quote_id,
                 warnings, terms_hash, state, expires_at, created_at)
            VALUES
                ('{{quoteId}}', '{{userId}}', '{{Guid.CreateVersion7()}}', 'buy', 1, 100, 100, 0,
                 0, 100, 'USD', 'fake', 'gq-1', '', 'hash-1', 'open', now() + interval '30 seconds', now());
            """,
            TestContext.Current.CancellationToken);
        var count = await Database.ExecuteScalarAsync<long>(
            _fixture.AppConnectionString,
            $"SELECT count(*) FROM trading.quotes WHERE id = '{quoteId}'",
            TestContext.Current.CancellationToken);
        count.ShouldBe(1);

        await InsertOrderAsync(Guid.CreateVersion7(), userId, "idem-app", quoteId: quoteId, appRole: true);
        var orderCount = await Database.ExecuteScalarAsync<long>(
            _fixture.AppConnectionString,
            $"SELECT count(*) FROM trading.orders WHERE quote_id = '{quoteId}'",
            TestContext.Current.CancellationToken);
        orderCount.ShouldBe(1);

        await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $"INSERT INTO trading.trading_events (id, user_id, order_id, event_type, occurred_at) VALUES ('{Guid.CreateVersion7()}', '{userId}', NULL, 'order_placed', now())",
            TestContext.Current.CancellationToken);
        await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $"UPDATE trading.orders SET detail_code = 'probe' WHERE quote_id = '{quoteId}'",
            TestContext.Current.CancellationToken);
        await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $"DELETE FROM trading.quotes WHERE id = '{quoteId}'",
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task AppRole_CannotUpdateOrDeleteTradingEvents(string operation)
    {
        var eventId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            _fixture.OwnerConnectionString,
            $"INSERT INTO trading.trading_events (id, user_id, order_id, event_type, occurred_at) VALUES ('{eventId}', '{userId}', NULL, 'order_placed', now())",
            TestContext.Current.CancellationToken);

        var sql = operation == "UPDATE"
            ? $"UPDATE trading.trading_events SET detail_code = 'x' WHERE id = '{eventId}'"
            : $"DELETE FROM trading.trading_events WHERE id = '{eventId}'";
        var exception = await Should.ThrowAsync<PostgresException>(
            () => Database.ExecuteAsync(_fixture.AppConnectionString, sql, TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe("42501");
    }

    [Theory]
    [InlineData("CREATE TABLE trading.forbidden_probe (id int)")]
    [InlineData("ALTER TABLE trading.orders ADD COLUMN forbidden int")]
    [InlineData("DROP TABLE trading.orders")]
    public async Task AppRole_CannotAlterTradingSchema(string sql)
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            () => Database.ExecuteAsync(_fixture.AppConnectionString, sql, TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task AppRole_CanReadTradingMigrationHistory()
    {
        var count = await Database.ExecuteScalarAsync<long>(
            _fixture.AppConnectionString,
            "SELECT count(*) FROM trading.__ef_migrations_history",
            TestContext.Current.CancellationToken);
        count.ShouldBeGreaterThan(0);
    }

    private async Task InsertOrderAsync(
        Guid orderId,
        Guid userId,
        string idempotencyKey,
        Guid? quoteId = null,
        bool appRole = false)
    {
        var connection = appRole ? _fixture.AppConnectionString : _fixture.OwnerConnectionString;
        await Database.ExecuteAsync(
            connection,
            $$"""
            INSERT INTO trading.orders
                (id, user_id, quote_id, token_id, instrument_id, side, state, settlement_asset,
                 quantity, price, gross_amount, our_fee, gateway_fee, total, idempotency_key,
                 gateway_key, created_at, updated_at)
            VALUES
                ('{{orderId}}', '{{userId}}', '{{quoteId ?? Guid.CreateVersion7()}}', '{{Guid.CreateVersion7()}}',
                 'INST-1', 'buy', 'pending_submission', 'USD',
                 1, 100, 100, 0, 0, 100, '{{idempotencyKey}}',
                 'fake', now(), now());
            """,
            TestContext.Current.CancellationToken);
    }
}

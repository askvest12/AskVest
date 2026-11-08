using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.MarketData.Persistence;
using AskVest.Platform;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.MarketData;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class MarketDataSchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_Reapply_As_NoOp()
    {
        var applied = await MigrationRunner.RunAsync(
            fixture.OwnerConnectionString,
            [connectionString => new MarketDataDbContext(MarketDataDbContextOptions.Create(connectionString))],
            TestContext.Current.CancellationToken);
        applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Database_Rejects_Non_Instrument_Subject_Type()
    {
        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.OwnerConnectionString,
                LatestPriceInsertSql(subjectType: "token"),
                TestContext.Current.CancellationToken));
        exception.Message.ShouldContain("ck_latest_prices_subject_type");
    }

    [Fact]
    public async Task Database_Rejects_Non_Positive_Price()
    {
        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.OwnerConnectionString,
                LatestPriceInsertSql(price: "0"),
                TestContext.Current.CancellationToken));
        exception.Message.ShouldContain("ck_latest_prices_price");
    }

    [Fact]
    public async Task Database_Rejects_Candle_With_High_Below_Low()
    {
        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.OwnerConnectionString,
                CandleInsertSql(high: "10", low: "20"),
                TestContext.Current.CancellationToken));
        exception.Message.ShouldContain("ck_candles_high_low");
    }

    [Fact]
    public async Task Database_Accepts_Valid_Price_And_Candle()
    {
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            LatestPriceInsertSql() + ";" + CandleInsertSql(),
            TestContext.Current.CancellationToken);

        var prices = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            "SELECT price FROM marketdata.latest_prices WHERE subject_id = 'instr-1'",
            TestContext.Current.CancellationToken);
        prices.Count.ShouldBe(1);
    }

    private static string LatestPriceInsertSql(
        string subjectType = "instrument",
        string subjectId = "instr-1",
        string price = "101.5") => $"""
            INSERT INTO marketdata.latest_prices
                (subject_type, subject_id, price, currency, as_of, received_at, session,
                 provider_key, delay_minutes, updated_at)
            VALUES ('{subjectType}', '{subjectId}', {price}, 'USD', now(), now(), 'regular',
                    'fake', 0, now())
            """;

    private static string CandleInsertSql(
        string interval = "1d",
        string high = "110",
        string low = "90") => $"""
            INSERT INTO marketdata.candles
                (subject_type, subject_id, interval, open_time, open, high, low, close,
                 volume, adjusted, partial, provider_key, fetched_at)
            VALUES ('instrument', 'instr-1', '{interval}', '2026-01-01T00:00:00Z',
                    100, {high}, {low}, 105, 1000, true, false, 'fake', now())
            """;
}

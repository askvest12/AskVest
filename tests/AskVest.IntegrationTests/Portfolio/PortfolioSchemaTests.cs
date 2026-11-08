using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Portfolio.Persistence;
using AskVest.Platform;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Portfolio;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PortfolioSchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_Reapply_As_NoOp()
    {
        var applied = await MigrationRunner.RunAsync(
            fixture.OwnerConnectionString,
            [connectionString => new PortfolioDbContext(PortfolioDbContextOptions.Create(connectionString))],
            TestContext.Current.CancellationToken);
        applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Composite_Key_Rejects_Duplicate_Watchlist_Item()
    {
        var userId = Guid.CreateVersion7();
        await InsertWatchlistAsync(userId, "inst-1");

        var exception = await Should.ThrowAsync<PostgresException>(() => InsertWatchlistAsync(userId, "inst-1"));
        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Same_Instrument_Is_Allowed_For_Different_Users()
    {
        await InsertWatchlistAsync(Guid.CreateVersion7(), "inst-1");
        await InsertWatchlistAsync(Guid.CreateVersion7(), "inst-1");
    }

    [Fact]
    public async Task Application_Role_Can_Read_Write_And_Delete_Watchlist_Items()
    {
        var userId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            fixture.AppConnectionString,
            $"INSERT INTO portfolio.watchlist_items (user_id, instrument_id, added_at) VALUES ('{userId}', 'inst-1', now())",
            TestContext.Current.CancellationToken);

        var count = await Database.ExecuteScalarAsync<long>(
            fixture.AppConnectionString,
            $"SELECT COUNT(*) FROM portfolio.watchlist_items WHERE user_id = '{userId}'",
            TestContext.Current.CancellationToken);
        count.ShouldBe(1);

        await Database.ExecuteAsync(
            fixture.AppConnectionString,
            $"DELETE FROM portfolio.watchlist_items WHERE user_id = '{userId}'",
            TestContext.Current.CancellationToken);

        var remaining = await Database.ExecuteScalarAsync<long>(
            fixture.AppConnectionString,
            $"SELECT COUNT(*) FROM portfolio.watchlist_items WHERE user_id = '{userId}'",
            TestContext.Current.CancellationToken);
        remaining.ShouldBe(0);
    }

    private async Task InsertWatchlistAsync(Guid userId, string instrumentId)
    {
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"INSERT INTO portfolio.watchlist_items (user_id, instrument_id, added_at) VALUES ('{userId}', '{instrumentId}', now())",
            TestContext.Current.CancellationToken);
    }
}

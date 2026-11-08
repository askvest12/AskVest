using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Practice;
using AskVest.Modules.Practice.Persistence;
using AskVest.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Practice;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PracticeSchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_Reapply_As_NoOp()
    {
        var applied = await MigrationRunner.RunAsync(
            fixture.OwnerConnectionString,
            [connectionString => new PracticeDbContext(PracticeDbContextOptions.Create(connectionString))],
            TestContext.Current.CancellationToken);
        applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unique_Constraint_Rejects_Duplicate_Order_Idempotency_Key()
    {
        var userId = Guid.CreateVersion7();
        await InsertOrderAsync(userId, "key-1");

        var exception = await Should.ThrowAsync<PostgresException>(() => InsertOrderAsync(userId, "key-1"));
        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Same_Idempotency_Key_Is_Allowed_For_Different_Users()
    {
        await InsertOrderAsync(Guid.CreateVersion7(), "shared-key");
        await InsertOrderAsync(Guid.CreateVersion7(), "shared-key");
    }

    [Fact]
    public async Task Unique_Constraint_Rejects_Duplicate_Instrument_Asset_Code()
    {
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            "INSERT INTO practice.practice_instruments (instrument_id, asset_code, created_at) VALUES ('i-1', 'P.AAA', now())",
            TestContext.Current.CancellationToken);

        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.OwnerConnectionString,
                "INSERT INTO practice.practice_instruments (instrument_id, asset_code, created_at) VALUES ('i-2', 'P.AAA', now())",
                TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Application_Role_Cannot_Update_Or_Delete_Practice_Events()
    {
        var eventId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"INSERT INTO practice.practice_events (id, user_id, event_type, subject_id, occurred_at) VALUES ('{eventId}', '{Guid.CreateVersion7()}', 'account_started', '{Guid.CreateVersion7()}', now())",
            TestContext.Current.CancellationToken);

        var update = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.AppConnectionString,
                $"UPDATE practice.practice_events SET event_type = 'account_reset' WHERE id = '{eventId}'",
                TestContext.Current.CancellationToken));
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        var delete = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.AppConnectionString,
                $"DELETE FROM practice.practice_events WHERE id = '{eventId}'",
                TestContext.Current.CancellationToken));
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Application_Role_Can_Read_And_Write_Practice_Tables()
    {
        var userId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            fixture.AppConnectionString,
            $"INSERT INTO practice.practice_accounts (user_id, started_at, reset_count, starting_credits_used, updated_at) VALUES ('{userId}', now(), 0, 10000, now())",
            TestContext.Current.CancellationToken);

        var rows = await Database.QueryAsync(
            fixture.AppConnectionString,
            $"SELECT reset_count FROM practice.practice_accounts WHERE user_id = '{userId}'",
            TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Application_Role_Cannot_Alter_Schema()
    {
        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.AppConnectionString,
                "CREATE TABLE practice.should_not_exist (id int)",
                TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public void Invalid_Configuration_Stops_Startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = "Host=localhost;Database=unused",
                ["Practice:StartingCredits"] = "0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPracticeModule(configuration);
        using var provider = services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AskVest.Modules.Practice.Configuration.PracticeOptions>>().Value);
    }

    private async Task InsertOrderAsync(Guid userId, string idempotencyKey)
    {
        var orderId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"""
            INSERT INTO practice.practice_orders
                (id, user_id, instrument_id, side, type, state, requested_amount, sell_all,
                 idempotency_key, created_at)
            VALUES ('{orderId}', '{userId}', 'instr-1', 'buy', 'market', 'filled', 100, false,
                    '{idempotencyKey}', now())
            """,
            TestContext.Current.CancellationToken);
    }
}

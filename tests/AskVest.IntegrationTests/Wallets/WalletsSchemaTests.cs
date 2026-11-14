using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Wallets.Persistence;
using AskVest.Platform;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Wallets;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class WalletsSchemaTests
{
    private readonly PostgresFixture _fixture;

    public WalletsSchemaTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migrations_Reapply_As_NoOp()
    {
        var applied = await MigrationRunner.RunAsync(
            _fixture.OwnerConnectionString,
            [connectionString => new WalletsDbContext(WalletsDbContextOptions.Create(connectionString))],
            TestContext.Current.CancellationToken);
        applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Provider_And_Deposit_Id_On_Deposits_Is_Unique()
    {
        await InsertDepositAsync("fake", "dep-1");
        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertDepositAsync("fake", "dep-1"));
        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task User_And_Idempotency_Key_On_Withdrawals_Is_Unique()
    {
        var userId = Guid.CreateVersion7();
        await InsertWithdrawalAsync(userId, "idem-1");
        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertWithdrawalAsync(userId, "idem-1"));
        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Wallet_Events_Cannot_Be_Updated_Or_Deleted_By_App_Role()
    {
        var eventId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $$"""
            INSERT INTO wallets.wallet_events
                (id, user_id, subject_type, subject_id, event_type, occurred_at)
            VALUES ('{{eventId}}', '{{Guid.CreateVersion7()}}', 'withdrawal', '{{Guid.CreateVersion7()}}', 'withdrawal_state_changed', now());
            """,
            TestContext.Current.CancellationToken);

        var update = await Should.ThrowAsync<PostgresException>(() => Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $"UPDATE wallets.wallet_events SET detail_code = 'x' WHERE id = '{eventId}'",
            TestContext.Current.CancellationToken));
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        var delete = await Should.ThrowAsync<PostgresException>(() => Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $"DELETE FROM wallets.wallet_events WHERE id = '{eventId}'",
            TestContext.Current.CancellationToken));
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task App_Role_Cannot_Alter_Wallets_Schema()
    {
        var exception = await Should.ThrowAsync<PostgresException>(() => Database.ExecuteAsync(
            _fixture.AppConnectionString,
            "ALTER TABLE wallets.deposits ADD COLUMN hacked text",
            TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private async Task InsertDepositAsync(string providerKey, string providerDepositId)
        => await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $$"""
            INSERT INTO wallets.deposits
                (id, provider_key, provider_deposit_id, user_id, chain_id, token_id,
                 quantity, sender_address, state, detected_at)
            VALUES
                ('{{Guid.CreateVersion7()}}', '{{providerKey}}', '{{providerDepositId}}',
                 '{{Guid.CreateVersion7()}}', 'eip155:1', '{{Guid.CreateVersion7()}}',
                 1, 'sender', 'pending', now());
            """,
            TestContext.Current.CancellationToken);

    private async Task InsertWithdrawalAsync(Guid userId, string idempotencyKey)
        => await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $$"""
            INSERT INTO wallets.withdrawals
                (id, user_id, token_id, address_id, destination_chain_id, destination_address,
                 gross_quantity, fee, net_quantity, terms_hash, state, expires_at,
                 idempotency_key, preview_used, created_at, updated_at)
            VALUES
                ('{{Guid.CreateVersion7()}}', '{{userId}}', '{{Guid.CreateVersion7()}}', '{{Guid.CreateVersion7()}}',
                 'eip155:1', 'dest', 10, 0, 10, 'hash', 'previewed', now() + interval '1 minute',
                 '{{idempotencyKey}}', false, now(), now());
            """,
            TestContext.Current.CancellationToken);
}

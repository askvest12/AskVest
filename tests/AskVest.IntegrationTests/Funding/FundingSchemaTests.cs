using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Funding.Persistence;
using AskVest.Platform;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Funding;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class FundingSchemaTests
{
    private readonly PostgresFixture _fixture;

    public FundingSchemaTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migrations_Reapply_As_NoOp()
    {
        var applied = await MigrationRunner.RunAsync(
            _fixture.OwnerConnectionString,
            [connectionString => new FundingDbContext(FundingDbContextOptions.Create(connectionString))],
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
    public async Task Provider_And_Event_Id_On_Webhook_Events_Is_Unique()
    {
        await InsertWebhookEventAsync("fake", "evt-1");
        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertWebhookEventAsync("fake", "evt-1"));
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
    public async Task Funding_Events_Cannot_Be_Updated_Or_Deleted_By_App_Role()
    {
        var eventId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $$"""
            INSERT INTO funding.funding_events
                (id, user_id, subject_type, subject_id, event_type, occurred_at)
            VALUES ('{{eventId}}', '{{Guid.CreateVersion7()}}', 'deposit', '{{Guid.CreateVersion7()}}', 'deposit_state_changed', now());
            """,
            TestContext.Current.CancellationToken);

        var update = await Should.ThrowAsync<PostgresException>(() => Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $"UPDATE funding.funding_events SET detail_code = 'x' WHERE id = '{eventId}'",
            TestContext.Current.CancellationToken));
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        var delete = await Should.ThrowAsync<PostgresException>(() => Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $"DELETE FROM funding.funding_events WHERE id = '{eventId}'",
            TestContext.Current.CancellationToken));
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task App_Role_Cannot_Alter_Funding_Schema()
    {
        var exception = await Should.ThrowAsync<PostgresException>(() => Database.ExecuteAsync(
            _fixture.AppConnectionString,
            "ALTER TABLE funding.deposits ADD COLUMN hacked text",
            TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private async Task InsertDepositAsync(string providerKey, string providerDepositId)
        => await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $$"""
            INSERT INTO funding.deposits
                (id, provider_key, provider_deposit_id, user_id, currency, amount,
                 provider_fee, payer_verified, state, shortfall, created_at, updated_at)
            VALUES
                ('{{Guid.CreateVersion7()}}', '{{providerKey}}', '{{providerDepositId}}',
                 '{{Guid.CreateVersion7()}}', 'USD', 10, 0, true, 'pending', 0, now(), now());
            """,
            TestContext.Current.CancellationToken);

    private async Task InsertWebhookEventAsync(string providerKey, string eventId)
        => await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $$"""
            INSERT INTO funding.webhook_events
                (id, provider_key, event_id, received_at, outcome, body_hash)
            VALUES
                ('{{Guid.CreateVersion7()}}', '{{providerKey}}', '{{eventId}}', now(), 'processed', 'hash');
            """,
            TestContext.Current.CancellationToken);

    private async Task InsertWithdrawalAsync(Guid userId, string idempotencyKey)
        => await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $$"""
            INSERT INTO funding.withdrawals
                (id, user_id, currency, beneficiary_id, receive_amount, fee_amount,
                 total_debited, terms_hash, state, expires_at, idempotency_key,
                 preview_used, created_at, updated_at)
            VALUES
                ('{{Guid.CreateVersion7()}}', '{{userId}}', 'USD', '{{Guid.CreateVersion7()}}',
                 10, 0, 10, 'hash', 'previewed', now() + interval '1 minute',
                 '{{idempotencyKey}}', false, now(), now());
            """,
            TestContext.Current.CancellationToken);
}

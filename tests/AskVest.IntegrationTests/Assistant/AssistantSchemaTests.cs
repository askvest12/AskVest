using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Assistant.Persistence;
using AskVest.Platform;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Assistant;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class AssistantSchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_Reapply_As_NoOp()
    {
        var applied = await MigrationRunner.RunAsync(
            fixture.OwnerConnectionString,
            [connectionString => new AssistantDbContext(AssistantDbContextOptions.Create(connectionString))],
            TestContext.Current.CancellationToken);
        applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unique_Constraint_Rejects_Duplicate_User_Idempotency_Key()
    {
        var userId = Guid.CreateVersion7();
        await InsertMessageAsync(userId, "key-1");

        var exception = await Should.ThrowAsync<PostgresException>(() => InsertMessageAsync(userId, "key-1"));
        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Same_Idempotency_Key_Is_Allowed_For_Different_Users()
    {
        await InsertMessageAsync(Guid.CreateVersion7(), "shared-key");
        await InsertMessageAsync(Guid.CreateVersion7(), "shared-key");
    }

    [Fact]
    public async Task Null_Idempotency_Keys_Do_Not_Conflict()
    {
        var userId = Guid.CreateVersion7();
        await InsertMessageAsync(userId, null);
        await InsertMessageAsync(userId, null);
    }

    [Fact]
    public async Task Application_Role_Cannot_Update_Or_Delete_Audit_Events()
    {
        var auditId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"""
            INSERT INTO assistant.audit_events
                (id, user_id, conversation_id, message_id, model_id, policy_version, outcome,
                 tool_names, citation_source_ids, tool_result_hashes,
                 input_tokens, output_tokens, latency_milliseconds, created_at)
            VALUES ('{auditId}', NULL, NULL, NULL, 'fake-1', 'prompt-1', 'answered',
                 '[]'::jsonb, '[]'::jsonb, '[]'::jsonb, 1, 1, 1, now())
            """,
            TestContext.Current.CancellationToken);

        var update = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.AppConnectionString,
                $"UPDATE assistant.audit_events SET outcome = 'x' WHERE id = '{auditId}'",
                TestContext.Current.CancellationToken));
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        var delete = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.AppConnectionString,
                $"DELETE FROM assistant.audit_events WHERE id = '{auditId}'",
                TestContext.Current.CancellationToken));
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Application_Role_Can_Read_And_Write_Conversations_And_Messages()
    {
        var userId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            fixture.AppConnectionString,
            $"""
            INSERT INTO assistant.conversations (id, user_id, title, created_at, updated_at)
            VALUES ('{conversationId}', '{userId}', 'chat', now(), now());
            INSERT INTO assistant.messages
                (id, conversation_id, user_id, role, text, citations, fact_cards, actions,
                 policy_outcome, created_at, idempotency_key)
            VALUES ('{Guid.CreateVersion7()}', '{conversationId}', '{userId}', 'user', 'hi',
                 '[]'::jsonb, '[]'::jsonb, '[]'::jsonb, NULL, now(), 'app-key')
            """,
            TestContext.Current.CancellationToken);

        var rows = await Database.QueryAsync(
            fixture.AppConnectionString,
            $"SELECT role FROM assistant.messages WHERE conversation_id = '{conversationId}'",
            TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Application_Role_Can_Insert_Audit_Events_But_Not_Read_Other_Schemas()
    {
        await Database.ExecuteAsync(
            fixture.AppConnectionString,
            $"""
            INSERT INTO assistant.audit_events
                (id, user_id, conversation_id, message_id, model_id, policy_version, outcome,
                 tool_names, citation_source_ids, tool_result_hashes,
                 input_tokens, output_tokens, latency_milliseconds, created_at)
            VALUES ('{Guid.CreateVersion7()}', NULL, NULL, NULL, 'fake-1', 'prompt-1', 'answered',
                 '[]'::jsonb, '[]'::jsonb, '[]'::jsonb, 0, 0, 0, now())
            """,
            TestContext.Current.CancellationToken);

        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.AppConnectionString,
                "CREATE TABLE assistant.should_not_exist (id int)",
                TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private async Task InsertMessageAsync(Guid userId, string? idempotencyKey)
    {
        var conversationId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"""
            INSERT INTO assistant.conversations (id, user_id, title, created_at, updated_at)
            VALUES ('{conversationId}', '{userId}', 'chat', now(), now());
            INSERT INTO assistant.messages
                (id, conversation_id, user_id, role, text, citations, fact_cards, actions,
                 policy_outcome, created_at, idempotency_key)
            VALUES ('{Guid.CreateVersion7()}', '{conversationId}', '{userId}', 'user', 'hi',
                 '[]'::jsonb, '[]'::jsonb, '[]'::jsonb, NULL, now(), {(idempotencyKey is null ? "NULL" : $"'{idempotencyKey}'")})
            """,
            TestContext.Current.CancellationToken);
    }
}

using AskVest.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PlatformSchemaTests
{
    private readonly PostgresFixture _fixture;

    public PlatformSchemaTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IdempotencyKeys_HasExpectedColumns()
    {
        var rows = await Database.QueryAsync(
            _fixture.OwnerConnectionString,
            """
            SELECT column_name, data_type, character_maximum_length, is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'platform' AND table_name = 'idempotency_keys'
            ORDER BY ordinal_position
            """,
            TestContext.Current.CancellationToken);

        var columns = rows.ToDictionary(r => (string)r["column_name"]!, r => r);
        columns.Keys.ShouldBe(
        [
            "id", "scope", "idempotency_key", "request_method", "request_path",
            "request_fingerprint", "state", "locked_until", "response_status_code",
            "response_content_type", "response_body", "response_body_omitted",
            "created_at", "completed_at", "expires_at",
        ], ignoreOrder: true);

        columns["request_fingerprint"]["data_type"].ShouldBe("character");
        columns["request_fingerprint"]["character_maximum_length"].ShouldBe(64L);
        columns["state"]["character_maximum_length"].ShouldBe(16L);
        columns["id"]["data_type"].ShouldBe("uuid");
        columns["created_at"]["data_type"].ShouldBe("timestamp with time zone");
        columns["response_body"]["data_type"].ShouldBe("text");
    }

    [Fact]
    public async Task IdempotencyKeys_HasExpectedIndexes()
    {
        var rows = await Database.QueryAsync(
            _fixture.OwnerConnectionString,
            "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'platform' AND tablename = 'idempotency_keys'",
            TestContext.Current.CancellationToken);

        var definitions = rows.Select(r => (string)r["indexdef"]!).ToList();
        definitions.ShouldContain(d => d.Contains("UNIQUE") && d.Contains("scope") && d.Contains("idempotency_key"));
        definitions.ShouldContain(d => d.Contains("expires_at") && !d.Contains("UNIQUE"));
        definitions.ShouldContain(d => d.Contains("pk_idempotency_keys"));
    }

    [Fact]
    public async Task IdempotencyKeys_HasCheckConstraints()
    {
        var rows = await Database.QueryAsync(
            _fixture.OwnerConnectionString,
            """
            SELECT conname, pg_get_constraintdef(oid) AS definition
            FROM pg_constraint
            WHERE conrelid = 'platform.idempotency_keys'::regclass AND contype = 'c'
            """,
            TestContext.Current.CancellationToken);

        var definitions = rows.Select(r => (string)r["definition"]!).ToList();
        definitions.ShouldContain(d => d.Contains("in_progress") && d.Contains("completed"));
        definitions.ShouldContain(d => d.Contains("response_status_code"));
    }
}

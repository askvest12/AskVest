using AskVest.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LeastPrivilegeTests
{
    private readonly PostgresFixture _fixture;

    public LeastPrivilegeTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private const string InsertSql = """
        INSERT INTO platform.idempotency_keys
            (id, scope, idempotency_key, request_method, request_path, request_fingerprint,
             state, response_body_omitted, created_at, expires_at)
        VALUES
            (gen_random_uuid(), 'test', 'privilege-probe', 'POST', '/probe',
             '0000000000000000000000000000000000000000000000000000000000000000',
             'in_progress', false, now(), now() + interval '1 hour')
        """;

    [Fact]
    public async Task AppRole_CanReadWriteIdempotencyKeys()
    {
        await Database.ExecuteAsync(_fixture.AppConnectionString, InsertSql, TestContext.Current.CancellationToken);
        var count = await Database.ExecuteScalarAsync<long>(
            _fixture.AppConnectionString,
            "SELECT count(*) FROM platform.idempotency_keys WHERE idempotency_key = 'privilege-probe'",
            TestContext.Current.CancellationToken);
        count.ShouldBe(1);

        await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            "UPDATE platform.idempotency_keys SET locked_until = now() WHERE idempotency_key = 'privilege-probe'",
            TestContext.Current.CancellationToken);
        await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            "DELETE FROM platform.idempotency_keys WHERE idempotency_key = 'privilege-probe'",
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("CREATE TABLE platform.forbidden_probe (id int)")]
    [InlineData("DROP TABLE platform.idempotency_keys")]
    [InlineData("ALTER TABLE platform.idempotency_keys ADD COLUMN forbidden int")]
    [InlineData("CREATE SCHEMA forbidden_schema")]
    public async Task AppRole_DdlIsRejected(string sql)
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            () => Database.ExecuteAsync(_fixture.AppConnectionString, sql, TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task AppRole_CanReadMigrationHistory()
    {
        var count = await Database.ExecuteScalarAsync<long>(
            _fixture.AppConnectionString,
            "SELECT count(*) FROM platform.__ef_migrations_history",
            TestContext.Current.CancellationToken);
        count.ShouldBeGreaterThan(0);
    }
}

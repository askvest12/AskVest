using AskVest.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdempotencyConstraintsTests
{
    private readonly PostgresFixture _fixture;

    public IdempotencyConstraintsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static string InsertSql(string key, string state, bool withStatusCode) => $$"""
        INSERT INTO platform.idempotency_keys
            (id, scope, idempotency_key, request_method, request_path, request_fingerprint,
             state, response_status_code, response_body_omitted, created_at, expires_at)
        VALUES
            (gen_random_uuid(), 'test', '{{key}}', 'POST', '/probe',
             '0000000000000000000000000000000000000000000000000000000000000000',
             '{{state}}', {{(withStatusCode ? "200" : "NULL")}}, false, now(), now() + interval '1 hour')
        """;

    [Fact]
    public async Task CompletedWithoutStatusCode_ViolatesCheckConstraint()
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            () => Database.ExecuteAsync(
                _fixture.OwnerConnectionString,
                InsertSql("ck-probe", "completed", false),
                TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task InvalidState_ViolatesCheckConstraint()
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            () => Database.ExecuteAsync(
                _fixture.OwnerConnectionString,
                InsertSql("ck-probe-2", "bogus", false),
                TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task DuplicateScopeAndKey_ViolatesUniqueIndex()
    {
        await Database.ExecuteAsync(
            _fixture.OwnerConnectionString,
            InsertSql("dup-probe", "in_progress", false),
            TestContext.Current.CancellationToken);
        var exception = await Should.ThrowAsync<PostgresException>(
            () => Database.ExecuteAsync(
                _fixture.OwnerConnectionString,
                InsertSql("dup-probe", "in_progress", false),
                TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe("23505");
    }
}

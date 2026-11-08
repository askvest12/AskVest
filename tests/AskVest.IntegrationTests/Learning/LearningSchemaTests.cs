using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Learning;
using AskVest.Modules.Learning.Persistence;
using AskVest.Platform;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Learning;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LearningSchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_Reapply_As_NoOp()
    {
        var applied = await MigrationRunner.RunAsync(
            fixture.OwnerConnectionString,
            [connectionString => new LearningDbContext(LearningDbContextOptions.Create(connectionString))],
            TestContext.Current.CancellationToken);
        applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unique_Constraint_Rejects_Duplicate_Attempt_Idempotency_Key()
    {
        var userId = Guid.CreateVersion7();
        await InsertAttemptAsync(userId, "key-1");

        var exception = await Should.ThrowAsync<PostgresException>(() => InsertAttemptAsync(userId, "key-1"));
        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Same_Idempotency_Key_Is_Allowed_For_Different_Users()
    {
        await InsertAttemptAsync(Guid.CreateVersion7(), "shared-key");
        await InsertAttemptAsync(Guid.CreateVersion7(), "shared-key");
    }

    [Fact]
    public async Task Unique_Constraint_Rejects_Duplicate_Learning_Day()
    {
        var userId = Guid.CreateVersion7();
        await InsertDayAsync(userId, "2026-11-08");

        var exception = await Should.ThrowAsync<PostgresException>(() => InsertDayAsync(userId, "2026-11-08"));
        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Application_Role_Cannot_Update_Or_Delete_Quiz_Attempts()
    {
        var attemptId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"""
            INSERT INTO learning.quiz_attempts
                (id, user_id, lesson_id, lesson_version, score, passed, answers, submitted_at, idempotency_key)
            VALUES ('{attemptId}', '{userId}', 'lesson-1', 1, 100, true, '[]'::jsonb, now(), 'key-x')
            """,
            TestContext.Current.CancellationToken);

        var update = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.AppConnectionString,
                $"UPDATE learning.quiz_attempts SET score = 0 WHERE id = '{attemptId}'",
                TestContext.Current.CancellationToken));
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        var delete = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.AppConnectionString,
                $"DELETE FROM learning.quiz_attempts WHERE id = '{attemptId}'",
                TestContext.Current.CancellationToken));
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Application_Role_Can_Read_And_Write_Learning_Tables()
    {
        var userId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            fixture.AppConnectionString,
            $"INSERT INTO learning.lesson_progress (user_id, lesson_id, status, attempts_count, updated_at) VALUES ('{userId}', 'lesson-1', 'attempted', 1, now())",
            TestContext.Current.CancellationToken);
        await Database.ExecuteAsync(
            fixture.AppConnectionString,
            $"INSERT INTO learning.learning_days (user_id, day, first_activity_at) VALUES ('{userId}', '2026-11-08', now())",
            TestContext.Current.CancellationToken);

        var rows = await Database.QueryAsync(
            fixture.AppConnectionString,
            $"SELECT status FROM learning.lesson_progress WHERE user_id = '{userId}'",
            TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Application_Role_Cannot_Alter_Schema()
    {
        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.AppConnectionString,
                "CREATE TABLE learning.should_not_exist (id int)",
                TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public void Invalid_Content_Stops_Startup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"learning-bad-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            LearningTestContent.WriteContent(directory, """{"tracks":[{"id":"T","title":"x","summary":"y","lessons":[]}]}""");
            Should.Throw<InvalidOperationException>(() => LearningTestServices.CreateProvider(fixture, TimeProvider.System, directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task InsertAttemptAsync(Guid userId, string idempotencyKey)
    {
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"""
            INSERT INTO learning.quiz_attempts
                (id, user_id, lesson_id, lesson_version, score, passed, answers, submitted_at, idempotency_key)
            VALUES ('{Guid.CreateVersion7()}', '{userId}', 'lesson-1', 1, 50, false, '[]'::jsonb, now(), '{idempotencyKey}')
            """,
            TestContext.Current.CancellationToken);
    }

    private async Task InsertDayAsync(Guid userId, string day)
    {
        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"INSERT INTO learning.learning_days (user_id, day, first_activity_at) VALUES ('{userId}', '{day}', now())",
            TestContext.Current.CancellationToken);
    }
}

using System.Security.Cryptography;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Identity.Persistence;
using AskVest.Platform;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Identity;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdentitySchemaTests(PostgresFixture fixture)
{
    private static readonly string[] ExpectedTables =
    [
        "users", "user_roles", "devices", "sessions", "refresh_tokens",
        "one_time_codes", "throttle_buckets", "step_up_grants", "security_events",
        "__ef_migrations_history",
    ];

    private static readonly string[] ExpectedUniqueIndexes =
    [
        "ix_users_normalized_email",
        "ix_users_phone_e164",
        "ix_devices_user_id_installation_id",
        "ix_refresh_tokens_token_hash",
        "ix_step_up_grants_token_hash",
        "ix_throttle_buckets_kind_key_hash",
    ];

    private static readonly string[] ExpectedCheckConstraints =
    [
        "ck_users_status",
        "ck_one_time_codes_purpose",
        "ck_one_time_codes_destination_type",
        "ck_throttle_buckets_kind",
        "ck_security_events_event_type",
    ];

    [Fact]
    public async Task Migrations_Are_Idempotent_On_Second_Run()
    {
        var applied = await MigrationRunner.RunAsync(
            fixture.OwnerConnectionString,
            [connectionString => new IdentityDbContext(IdentityDbContextOptions.Create(connectionString))],
            TestContext.Current.CancellationToken);
        applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Schema_Contains_All_Tables()
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'identity' ORDER BY table_name;",
            connection);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        tables.ShouldBe(ExpectedTables.OrderBy(t => t, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task Schema_Contains_Unique_Indexes_And_Check_Constraints()
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var indexes = await QueryStringsAsync(connection,
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'identity' AND indexdef LIKE '%UNIQUE%';");
        foreach (var expected in ExpectedUniqueIndexes)
        {
            indexes.ShouldContain(expected);
        }

        var constraints = await QueryStringsAsync(connection,
            "SELECT conname FROM pg_constraint c JOIN pg_class t ON c.conrelid = t.oid JOIN pg_namespace n ON t.relnamespace = n.oid WHERE n.nspname = 'identity' AND c.contype = 'c';");
        foreach (var expected in ExpectedCheckConstraints)
        {
            constraints.ShouldContain(expected);
        }
    }

    [Fact]
    public async Task Phone_Unique_Index_Is_Filtered_To_Verified_Phones()
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'identity' AND indexname = 'ix_users_phone_e164';",
            connection);
        var definition = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        definition.ShouldNotBeNull();
        definition.ShouldContain("phone_verified_at IS NOT NULL");
    }

    [Fact]
    public async Task Application_Role_Can_Write_All_Tables_But_Not_Ddl()
    {
        var userId = Guid.CreateVersion7();
        var deviceId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        var now = TimeProvider.System.GetUtcNow();

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await ExecuteAsync(connection,
            "INSERT INTO identity.users (id, normalized_email, password_hash, password_pepper_version, status, pin_failed_count, created_at, updated_at) VALUES (@id, @email, 'hash', 1, 'PendingVerification', 0, @now, @now)",
            ("id", userId), ("email", $"app-{userId:N}@example.com"), ("now", now));
        await ExecuteAsync(connection,
            "INSERT INTO identity.user_roles (user_id, role_name) VALUES (@user, 'user')",
            ("user", userId));
        await ExecuteAsync(connection,
            "INSERT INTO identity.devices (id, user_id, installation_id, platform, device_name, first_seen_at, last_seen_at) VALUES (@id, @user, @install, 'ios', 'Test Device', @now, @now)",
            ("id", deviceId), ("user", userId), ("install", $"install-{userId:N}"), ("now", now));
        await ExecuteAsync(connection,
            "INSERT INTO identity.sessions (id, user_id, device_id, created_at, last_used_at, absolute_expires_at) VALUES (@id, @user, @device, @now, @now, @expiry)",
            ("id", sessionId), ("user", userId), ("device", deviceId), ("now", now), ("expiry", now.AddDays(90)));
        await ExecuteAsync(connection,
            "INSERT INTO identity.refresh_tokens (id, session_id, token_hash, issued_at, idle_expires_at) VALUES (@id, @session, @hash, @now, @expiry)",
            ("id", Guid.CreateVersion7()), ("session", sessionId), ("hash", Hash("token-" + sessionId)), ("now", now), ("expiry", now.AddDays(30)));
        await ExecuteAsync(connection,
            "INSERT INTO identity.one_time_codes (id, user_id, purpose, destination_type, destination, code_hash, attempts_used, attempts_allowed, expires_at, created_at) VALUES (@id, @user, 'EmailVerification', 'Email', @dest, @hash, 0, 5, @expiry, @now)",
            ("id", Guid.CreateVersion7()), ("user", userId), ("dest", $"app-{userId:N}@example.com"), ("hash", Hash("code-" + userId)), ("expiry", now.AddMinutes(10)), ("now", now));
        await ExecuteAsync(connection,
            "INSERT INTO identity.throttle_buckets (id, kind, key_hash, failure_count, window_start, lock_repeat_count) VALUES (@id, 'Account', @hash, 1, @now, 0)",
            ("id", Guid.CreateVersion7()), ("hash", Hash("bucket-" + userId)), ("now", now));
        await ExecuteAsync(connection,
            "INSERT INTO identity.step_up_grants (id, user_id, session_id, purpose, token_hash, expires_at, created_at) VALUES (@id, @user, @session, 'delete_account', @hash, @expiry, @now)",
            ("id", Guid.CreateVersion7()), ("user", userId), ("session", sessionId), ("hash", Hash("grant-" + sessionId)), ("expiry", now.AddSeconds(120)), ("now", now));
        await ExecuteAsync(connection,
            "INSERT INTO identity.security_events (id, event_type, occurred_at) VALUES (@id, 'SignUp', @now)",
            ("id", Guid.CreateVersion7()), ("now", now));

        var ddl = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(
                "CREATE TABLE identity.app_role_ddl_probe (id int);",
                connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });
        ddl.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task Security_Events_Are_Append_Only_For_Application_Role()
    {
        var eventId = Guid.CreateVersion7();
        var now = TimeProvider.System.GetUtcNow();

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO identity.security_events (id, event_type, occurred_at) VALUES (@id, 'LoginSucceeded', @now);",
            connection))
        {
            insert.Parameters.AddWithValue("id", eventId);
            insert.Parameters.AddWithValue("now", now);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var update = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(
                "UPDATE identity.security_events SET event_type = 'LoginFailed' WHERE id = @id;",
                connection);
            command.Parameters.AddWithValue("id", eventId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });
        update.SqlState.ShouldBe("42501");

        await using (var anonymizeColumns = new NpgsqlCommand(
            "UPDATE identity.security_events SET ip = NULL, user_agent = NULL WHERE id = @id;",
            connection))
        {
            anonymizeColumns.Parameters.AddWithValue("id", eventId);
            await anonymizeColumns.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var delete = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(
                "DELETE FROM identity.security_events WHERE id = @id;",
                connection);
            command.Parameters.AddWithValue("id", eventId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });
        delete.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task Duplicate_Normalized_Email_Is_Rejected()
    {
        var email = $"dup-{Guid.CreateVersion7():N}@example.com";
        var now = TimeProvider.System.GetUtcNow();
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await InsertUserAsync(connection, email, now);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertUserAsync(connection, email, now));
        exception.SqlState.ShouldBe("23505");
    }

    [Fact]
    public async Task Duplicate_Token_Hash_Is_Rejected()
    {
        var now = TimeProvider.System.GetUtcNow();
        var userId = Guid.CreateVersion7();
        var deviceId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        var tokenHash = Hash("shared-token");

        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await InsertUserAsync(connection, $"tok-{userId:N}@example.com", now, userId);
        await ExecuteAsync(connection,
            "INSERT INTO identity.devices (id, user_id, installation_id, platform, device_name, first_seen_at, last_seen_at) VALUES (@id, @user, 'i1', 'ios', 'd', @now, @now)",
            ("id", deviceId), ("user", userId), ("now", now));
        await ExecuteAsync(connection,
            "INSERT INTO identity.sessions (id, user_id, device_id, created_at, last_used_at, absolute_expires_at) VALUES (@id, @user, @device, @now, @now, @expiry)",
            ("id", sessionId), ("user", userId), ("device", deviceId), ("now", now), ("expiry", now.AddDays(90)));
        await ExecuteAsync(connection,
            "INSERT INTO identity.refresh_tokens (id, session_id, token_hash, issued_at, idle_expires_at) VALUES (@id, @session, @hash, @now, @expiry)",
            ("id", Guid.CreateVersion7()), ("session", sessionId), ("hash", tokenHash), ("now", now), ("expiry", now.AddDays(30)));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection,
                "INSERT INTO identity.refresh_tokens (id, session_id, token_hash, issued_at, idle_expires_at) VALUES (@id, @session, @hash, @now, @expiry)",
                ("id", Guid.CreateVersion7()), ("session", sessionId), ("hash", tokenHash), ("now", now), ("expiry", now.AddDays(30))));
        exception.SqlState.ShouldBe("23505");
    }

    private static async Task InsertUserAsync(NpgsqlConnection connection, string email, DateTimeOffset now, Guid? id = null)
        => await ExecuteAsync(connection,
            "INSERT INTO identity.users (id, normalized_email, password_hash, password_pepper_version, status, pin_failed_count, created_at, updated_at) VALUES (@id, @email, 'hash', 1, 'PendingVerification', 0, @now, @now)",
            ("id", id ?? Guid.CreateVersion7()), ("email", email), ("now", now));

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> QueryStringsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var results = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}

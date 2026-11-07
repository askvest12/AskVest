using AskVest.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Compliance;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class CompliancePrivilegeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AppRole_CanInsertButNotUpdateOrDeleteComplianceEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            fixture.AppConnectionString,
            $"""
            INSERT INTO compliance.compliance_events (id, user_id, event_type, occurred_at)
            VALUES (gen_random_uuid(), '{userId}', 'ConsentAccepted', now())
            """,
            ct);

        var update = await Should.ThrowAsync<PostgresException>(() => Database.ExecuteAsync(
            fixture.AppConnectionString,
            "UPDATE compliance.compliance_events SET correlation_id = 'x'",
            ct));
        update.SqlState.ShouldBe("42501");

        var delete = await Should.ThrowAsync<PostgresException>(() => Database.ExecuteAsync(
            fixture.AppConnectionString,
            "DELETE FROM compliance.compliance_events",
            ct));
        delete.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task AppRole_CannotAlterComplianceSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        var createTable = await Should.ThrowAsync<PostgresException>(() => Database.ExecuteAsync(
            fixture.AppConnectionString,
            "CREATE TABLE compliance.forbidden_probe (id int)",
            ct));
        createTable.SqlState.ShouldBe("42501");

        var alterTable = await Should.ThrowAsync<PostgresException>(() => Database.ExecuteAsync(
            fixture.AppConnectionString,
            "ALTER TABLE compliance.compliance_profiles ADD COLUMN forbidden int",
            ct));
        alterTable.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task Duplicate_Consent_And_Webhook_Event_Are_Rejected_By_Database()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.CreateVersion7();
        const string consentSql = """
            INSERT INTO compliance.consent_records
                (id, user_id, document_type, version, content_hash, accepted_at)
            VALUES (gen_random_uuid(), '{0}', 'terms', 'v1', 'hash', now())
            """;
        await Database.ExecuteAsync(fixture.AppConnectionString, string.Format(consentSql, userId), ct);
        var duplicateConsent = await Should.ThrowAsync<PostgresException>(
            () => Database.ExecuteAsync(fixture.AppConnectionString, string.Format(consentSql, userId), ct));
        duplicateConsent.SqlState.ShouldBe("23505");

        const string webhookSql = """
            INSERT INTO compliance.kyc_webhook_events
                (id, provider, event_id, received_at, outcome, body_hash)
            VALUES (gen_random_uuid(), 'fake', 'dup-event', now(), 'Applied', 'hash')
            """;
        await Database.ExecuteAsync(fixture.AppConnectionString, webhookSql, ct);
        var duplicateWebhook = await Should.ThrowAsync<PostgresException>(
            () => Database.ExecuteAsync(fixture.AppConnectionString, webhookSql, ct));
        duplicateWebhook.SqlState.ShouldBe("23505");
    }

    [Fact]
    public async Task Compliance_Tables_And_Unique_Indexes_Exist()
    {
        var ct = TestContext.Current.CancellationToken;
        var tables = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            "SELECT tablename FROM pg_tables WHERE schemaname = 'compliance' ORDER BY tablename",
            ct);
        tables.Select(row => (string)row["tablename"]!).ShouldBe(
            ["__ef_migrations_history", "compliance_events", "compliance_profiles", "consent_records", "kyc_webhook_events"],
            ignoreOrder: true);

        var indexes = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            """
            SELECT indexname FROM pg_indexes
            WHERE schemaname = 'compliance' AND indexdef LIKE '%UNIQUE%'
            """,
            ct);
        var names = indexes.Select(row => (string)row["indexname"]!).ToList();
        names.ShouldContain("ix_consent_records_user_id_document_type_version");
        names.ShouldContain("ix_kyc_webhook_events_provider_event_id");
    }
}

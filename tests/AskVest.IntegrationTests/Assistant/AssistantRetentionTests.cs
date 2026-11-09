using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Assistant;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class AssistantRetentionTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = AssistantTestSetup.CreateFactory(fixture, retentionDays: 30);
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Retention_Removes_Old_And_Anonymized_Conversations_And_Keeps_Audit()
    {
        var oldUserToken = await AssistantTestSetup.AssistantUserAsync(_client, _factory);
        var oldUserId = AssistantTestSetup.UserIdFromToken(oldUserToken);
        var oldData = await AssistantTestSetup.SendAsync(_client, oldUserToken, "What is a tokenized stock?");
        var oldConversationId = oldData.GetProperty("conversationId").GetGuid();

        var anonymizedToken = await AssistantTestSetup.AssistantUserAsync(_client, _factory);
        var anonymizedUserId = AssistantTestSetup.UserIdFromToken(anonymizedToken);
        var anonymizedData = await AssistantTestSetup.SendAsync(
            _client, anonymizedToken, "What is a custodian?");
        var anonymizedConversationId = anonymizedData.GetProperty("conversationId").GetGuid();

        var keptToken = await AssistantTestSetup.AssistantUserAsync(_client, _factory);
        var keptUserId = AssistantTestSetup.UserIdFromToken(keptToken);
        var keptData = await AssistantTestSetup.SendAsync(_client, keptToken, "What is on my watchlist?");
        var keptConversationId = keptData.GetProperty("conversationId").GetGuid();

        await AssistantTestSetup.AgeConversationsAsync(
            fixture, oldUserId, 40, TestContext.Current.CancellationToken);
        await AssistantTestSetup.SetUserStatusAsync(
            fixture, anonymizedUserId, "Anonymized", TestContext.Current.CancellationToken);

        var auditsBefore = await CountAuditAsync(oldConversationId);
        auditsBefore.ShouldBeGreaterThan(0);

        await AssistantTestSetup.RunRetentionAsync(_factory, TestContext.Current.CancellationToken);

        (await AssistantTestSetup.CountConversationsAsync(
            fixture, oldUserId, TestContext.Current.CancellationToken)).ShouldBe(0);
        (await AssistantTestSetup.CountConversationsAsync(
            fixture, anonymizedUserId, TestContext.Current.CancellationToken)).ShouldBe(0);
        (await AssistantTestSetup.CountConversationsAsync(
            fixture, keptUserId, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await CountAuditAsync(oldConversationId)).ShouldBe(auditsBefore);
        (await CountAuditAsync(anonymizedConversationId)).ShouldBeGreaterThan(0);
        (await CountAuditAsync(keptConversationId)).ShouldBeGreaterThan(0);

        using var detailResponse = await _client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/assistant/conversations/{keptConversationId}").Auth(keptToken),
            TestContext.Current.CancellationToken);
        detailResponse.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Retention_Reruns_Cleanly()
    {
        var token = await AssistantTestSetup.AssistantUserAsync(_client, _factory);
        var userId = AssistantTestSetup.UserIdFromToken(token);
        await AssistantTestSetup.SendAsync(_client, token, "What is a tokenized stock?");
        await AssistantTestSetup.AgeConversationsAsync(
            fixture, userId, 40, TestContext.Current.CancellationToken);

        await AssistantTestSetup.RunRetentionAsync(_factory, TestContext.Current.CancellationToken);
        (await AssistantTestSetup.CountConversationsAsync(
            fixture, userId, TestContext.Current.CancellationToken)).ShouldBe(0);

        await AssistantTestSetup.RunRetentionAsync(_factory, TestContext.Current.CancellationToken);
        (await AssistantTestSetup.CountConversationsAsync(
            fixture, userId, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    private async Task<int> CountAuditAsync(Guid conversationId)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM assistant.audit_events WHERE conversation_id = @id", connection);
        command.Parameters.AddWithValue("id", conversationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}

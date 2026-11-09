using System.Net;
using System.Text.Json;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Assistant.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Assistant;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class AssistantApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;
    private HttpClient _client = null!;
    private (string AaplId, string HostileId) _catalog;

    public async ValueTask InitializeAsync()
    {
        _factory = AssistantTestSetup.CreateFactory(fixture);
        _client = _factory.CreateClient();
        _catalog = await AssistantTestSetup.ImportCatalogAsync(fixture, TestContext.Current.CancellationToken);
        var probeToken = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        await ProbeMarketDataAsync(probeToken, "normal");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.DataAsync(response);

    private static async Task<JsonElement> EnvelopeAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.EnvelopeAsync(response);

    private async Task ProbeMarketDataAsync(string token, string scenario)
    {
        using var response = await _client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/diagnostics/marketdata-scenario", new { scenario }).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task ProbeAssistantAsync(string token, string scenario)
    {
        using var response = await _client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/diagnostics/assistant-scenario", new { scenario }).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<string> EligibleUserAsync()
        => await AssistantTestSetup.AssistantUserAsync(_client, _factory);

    [Fact]
    public async Task Messages_Require_Authentication()
    {
        using var response = await _client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/assistant/messages", new { text = "hello" }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Messages_Require_Practice_Eligibility()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        using var response = await _client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/assistant/messages", new { text = "hello" }).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var envelope = await EnvelopeAsync(response);
        ComplianceTestUsers.ErrorCode(envelope).ShouldBe("compliance.not_eligible");
    }

    [Fact]
    public async Task Messages_Require_Ai_Processing_Consent()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var client = _client;
        await ComplianceTestUsers.SetCountryAsync(client, token, "NG");
        await ComplianceTestUsers.AcceptDocumentsAsync(
            client, token,
            ComplianceTestUsers.AllDocuments.Where(document => document.Type != "ai_processing").ToArray());

        using var denied = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/assistant/messages", new { text = "hello" }).Auth(token),
            TestContext.Current.CancellationToken);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var deniedEnvelope = await EnvelopeAsync(denied);
        ComplianceTestUsers.ErrorCode(deniedEnvelope).ShouldBe("compliance.not_eligible");
        var details = deniedEnvelope.GetProperty("error").GetProperty("details").EnumerateArray().ToList();
        details.ShouldContain(detail =>
            detail.GetProperty("message").GetString()!.Contains("ai_processing", StringComparison.Ordinal));

        await ComplianceTestUsers.AcceptDocumentsAsync(
            client, token, [ComplianceTestUsers.AllDocuments[^1]]);
        var data = await AssistantTestSetup.SendAsync(client, token, "What is a tokenized stock?");
        data.GetProperty("text").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Message_Returns_Cited_Answer_And_Fact_Cards_Match_Market_Data()
    {
        var token = await EligibleUserAsync();
        var client = _client;

        var data = await AssistantTestSetup.SendAsync(
            client, token, $"What is the latest price for {_catalog.AaplId}?");
        var conversationId = data.GetProperty("conversationId").GetGuid();
        data.GetProperty("messageId").GetGuid().ShouldNotBe(Guid.Empty);
        var text = data.GetProperty("text").GetString()!;
        text.ShouldNotBeNullOrWhiteSpace();
        data.GetProperty("policy").GetProperty("blocked").GetBoolean().ShouldBeFalse();
        data.GetProperty("policy").GetProperty("version").GetString().ShouldBe("prompt-1");
        data.GetProperty("disclosure").GetString().ShouldNotBeNullOrWhiteSpace();

        var citations = data.GetProperty("citations").EnumerateArray().ToList();
        citations.ShouldNotBeEmpty();
        citations.ShouldContain(citation => citation.GetProperty("type").GetString() == "price");

        var factCards = data.GetProperty("factCards").EnumerateArray().ToList();
        factCards.ShouldContain(card => card.GetProperty("cardType").GetString() == "price");
        var priceCard = factCards.First(card => card.GetProperty("cardType").GetString() == "price");
        var cardPrice = priceCard.GetProperty("fields").EnumerateArray()
            .First(field => field.GetProperty("name").GetString() == "price")
            .GetProperty("value").GetString();

        using var pricesResponse = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/marketdata/prices?instrumentIds={_catalog.AaplId}").Auth(token),
            TestContext.Current.CancellationToken);
        pricesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var prices = await DataAsync(pricesResponse);
        var apiPrice = prices.EnumerateArray()
            .First(item => item.GetProperty("instrumentId").GetString() == _catalog.AaplId)
            .GetProperty("price").GetString();
        decimal.Parse(cardPrice!, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(decimal.Parse(apiPrice!, System.Globalization.CultureInfo.InvariantCulture));

        var second = await AssistantTestSetup.SendAsync(
            client, token, "What is on my watchlist?", conversationId);
        second.GetProperty("conversationId").GetGuid().ShouldBe(conversationId);
    }

    [Fact]
    public async Task Conversation_Of_Other_User_Is_Not_Found()
    {
        var owner = await EligibleUserAsync();
        var other = await EligibleUserAsync();
        var data = await AssistantTestSetup.SendAsync(_client, owner, "What is a tokenized stock?");
        var conversationId = data.GetProperty("conversationId").GetGuid();

        using var getResponse = await _client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/assistant/conversations/{conversationId}").Auth(other),
            TestContext.Current.CancellationToken);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var getEnvelope = await EnvelopeAsync(getResponse);
        ComplianceTestUsers.ErrorCode(getEnvelope).ShouldBe("resource.not_found");

        using var deleteResponse = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/assistant/conversations/{conversationId}")
                .Auth(other),
            TestContext.Current.CancellationToken);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Idempotent_Key_Replays_Without_New_Model_Call_Or_Message()
    {
        var token = await EligibleUserAsync();
        var client = _client;
        var fake = AssistantTestSetup.FakeModel(_factory);
        var key = $"assistant-{Guid.CreateVersion7():N}";
        var body = new { text = "What is a tokenized stock?" };

        var callsBefore = fake.CallCount;
        using var firstResponse = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/assistant/messages", body, key).Auth(token),
            TestContext.Current.CancellationToken);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstData = await DataAsync(firstResponse);
        var conversationId = firstData.GetProperty("conversationId").GetGuid();
        fake.CallCount.ShouldBeGreaterThan(callsBefore);

        var callsAfterFirst = fake.CallCount;
        using var replayResponse = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/assistant/messages", body, key).Auth(token),
            TestContext.Current.CancellationToken);
        replayResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var replayData = await DataAsync(replayResponse);
        fake.CallCount.ShouldBe(callsAfterFirst);
        replayData.GetRawText().ShouldBe(firstData.GetRawText());

        using var detailResponse = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/assistant/conversations/{conversationId}").Auth(token),
            TestContext.Current.CancellationToken);
        var detail = await DataAsync(detailResponse);
        detail.GetProperty("messages").GetArrayLength().ShouldBe(2);

        using var conflictResponse = await client.SendAsync(
            ComplianceTestUsers.Post(
                "/api/v1/assistant/messages",
                new { text = "a different question entirely" },
                key).Auth(token),
            TestContext.Current.CancellationToken);
        conflictResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var conflictEnvelope = await EnvelopeAsync(conflictResponse);
        ComplianceTestUsers.ErrorCode(conflictEnvelope).ShouldBe("idempotency.key_reused");
    }

    [Fact]
    public async Task Hourly_Rate_Limit_Returns_429_With_Retry_After_And_Audit()
    {
        await using var limitedFactory = AssistantTestSetup.CreateFactory(fixture, messagesPerHour: 2);
        using var limitedClient = limitedFactory.CreateClient();
        var token = await AssistantTestSetup.AssistantUserAsync(limitedClient, limitedFactory);

        await AssistantTestSetup.SendAsync(limitedClient, token, "What is a tokenized stock?");
        await AssistantTestSetup.SendAsync(limitedClient, token, "What is a tokenized stock?");

        var auditsBefore = await AssistantTestSetup.CountAuditEventsAsync(
            fixture, "rate_limited", TestContext.Current.CancellationToken);
        using var response = await limitedClient.SendAsync(
            ComplianceTestUsers.Post("/api/v1/assistant/messages", new { text = "one more" }).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.ShouldNotBeNull();
        var envelope = await EnvelopeAsync(response);
        ComplianceTestUsers.ErrorCode(envelope).ShouldBe("rate_limit.exceeded");
        (await AssistantTestSetup.CountAuditEventsAsync(
            fixture, "rate_limited", TestContext.Current.CancellationToken))
            .ShouldBeGreaterThan(auditsBefore);
    }

    [Fact]
    public async Task Daily_Rate_Limit_Returns_429()
    {
        await using var limitedFactory = AssistantTestSetup.CreateFactory(fixture, messagesPerDay: 2);
        using var limitedClient = limitedFactory.CreateClient();
        var token = await AssistantTestSetup.AssistantUserAsync(limitedClient, limitedFactory);

        await AssistantTestSetup.SendAsync(limitedClient, token, "What is a tokenized stock?");
        await AssistantTestSetup.SendAsync(limitedClient, token, "What is a tokenized stock?");

        using var response = await limitedClient.SendAsync(
            ComplianceTestUsers.Post("/api/v1/assistant/messages", new { text = "one more" }).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var envelope = await EnvelopeAsync(response);
        ComplianceTestUsers.ErrorCode(envelope).ShouldBe("rate_limit.exceeded");
    }

    [Fact]
    public async Task Rate_Limit_Resets_After_The_Window()
    {
        // The fake clock cannot drive this: access tokens are validated against
        // the real clock for nbf and the injected clock for exp, so any advance
        // past the token lifetime makes re-login impossible. Aging the stored
        // messages exercises the same sliding-window count.
        await using var limitedFactory = AssistantTestSetup.CreateFactory(fixture, messagesPerHour: 2);
        using var limitedClient = limitedFactory.CreateClient();
        var token = await AssistantTestSetup.AssistantUserAsync(limitedClient, limitedFactory);
        var userId = AssistantTestSetup.UserIdFromToken(token);

        await AssistantTestSetup.SendAsync(limitedClient, token, "What is a tokenized stock?");
        await AssistantTestSetup.SendAsync(limitedClient, token, "What is a tokenized stock?");
        using var limited = await limitedClient.SendAsync(
            ComplianceTestUsers.Post("/api/v1/assistant/messages", new { text = "one more" }).Auth(token),
            TestContext.Current.CancellationToken);
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        await AssistantTestSetup.AgeMessagesAsync(
            fixture, userId, TimeSpan.FromHours(2), TestContext.Current.CancellationToken);

        var data = await AssistantTestSetup.SendAsync(limitedClient, token, "What is a tokenized stock?");
        data.GetProperty("text").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Over_Long_And_Empty_Messages_Are_Rejected()
    {
        var token = await EligibleUserAsync();
        var client = _client;
        var longText = new string('x', 2001);
        using var longResponse = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/assistant/messages", new { text = longText }).Auth(token),
            TestContext.Current.CancellationToken);
        longResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var longEnvelope = await EnvelopeAsync(longResponse);
        ComplianceTestUsers.ErrorCode(longEnvelope).ShouldBe("validation.failed");

        using var emptyResponse = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/assistant/messages", new { text = "   " }).Auth(token),
            TestContext.Current.CancellationToken);
        emptyResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var emptyEnvelope = await EnvelopeAsync(emptyResponse);
        ComplianceTestUsers.ErrorCode(emptyEnvelope).ShouldBe("validation.failed");
    }

    [Fact]
    public async Task Kill_Switch_Returns_Assistant_Unavailable()
    {
        await using var disabledFactory = AssistantTestSetup.CreateFactory(fixture, assistantEnabled: false);
        using var disabledClient = disabledFactory.CreateClient();
        var token = await AssistantTestSetup.AssistantUserAsync(disabledClient, disabledFactory);

        using var response = await disabledClient.SendAsync(
            ComplianceTestUsers.Post("/api/v1/assistant/messages", new { text = "hello" }).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var envelope = await EnvelopeAsync(response);
        ComplianceTestUsers.ErrorCode(envelope).ShouldBe("assistant.unavailable");
    }

    [Fact]
    public async Task Conversations_List_Get_Delete_And_Audit_Remains()
    {
        var token = await EligibleUserAsync();
        var client = _client;
        var userId = AssistantTestSetup.UserIdFromToken(token);

        var first = await AssistantTestSetup.SendAsync(client, token, "What is a tokenized stock?");
        var firstId = first.GetProperty("conversationId").GetGuid();
        await AssistantTestSetup.SendAsync(client, token, "What is on my watchlist?", firstId);
        var second = await AssistantTestSetup.SendAsync(client, token, "What is a custodian?");
        var secondId = second.GetProperty("conversationId").GetGuid();
        secondId.ShouldNotBe(firstId);

        using var listResponse = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/assistant/conversations?limit=1").Auth(token),
            TestContext.Current.CancellationToken);
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await DataAsync(listResponse);
        page.GetProperty("items").GetArrayLength().ShouldBe(1);
        page.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        var cursor = page.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();

        using var page2Response = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/assistant/conversations?limit=1&cursor={Uri.EscapeDataString(cursor!)}")
                .Auth(token),
            TestContext.Current.CancellationToken);
        var page2 = await DataAsync(page2Response);
        page2.GetProperty("items").GetArrayLength().ShouldBe(1);
        page2.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
        var ids = page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .Concat(page2.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid()))
            .ToList();
        ids.ShouldBe([secondId, firstId], ignoreOrder: false);

        using var detailResponse = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/assistant/conversations/{firstId}").Auth(token),
            TestContext.Current.CancellationToken);
        var detail = await DataAsync(detailResponse);
        detail.GetProperty("id").GetGuid().ShouldBe(firstId);
        var roles = detail.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("role").GetString())
            .ToList();
        roles.ShouldBe(["user", "assistant", "user", "assistant"]);

        var auditsBefore = await AuditCountForConversationAsync(firstId);
        auditsBefore.ShouldBeGreaterThan(0);
        using var deleteResponse = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/assistant/conversations/{firstId}")
                .Auth(token),
            TestContext.Current.CancellationToken);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var deleteAgain = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/assistant/conversations/{firstId}")
                .Auth(token),
            TestContext.Current.CancellationToken);
        deleteAgain.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AuditCountForConversationAsync(firstId)).ShouldBe(auditsBefore);
        (await AssistantTestSetup.CountConversationsAsync(
            fixture, userId, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    private async Task<int> AuditCountForConversationAsync(Guid conversationId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*) FROM assistant.audit_events WHERE conversation_id = @id", connection);
        command.Parameters.AddWithValue("id", conversationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Provider_Error_Stores_No_Messages()
    {
        var token = await EligibleUserAsync();
        var client = _client;
        var userId = AssistantTestSetup.UserIdFromToken(token);
        var conversationsBefore = await AssistantTestSetup.CountConversationsAsync(
            fixture, userId, TestContext.Current.CancellationToken);

        await ProbeAssistantAsync(token, "error");
        try
        {
            using var response = await client.SendAsync(
                ComplianceTestUsers.Post("/api/v1/assistant/messages", new { text = "hello" }).Auth(token),
                TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            var envelope = await EnvelopeAsync(response);
            ComplianceTestUsers.ErrorCode(envelope).ShouldBe("assistant.unavailable");
        }
        finally
        {
            await ProbeAssistantAsync(token, "normal");
        }

        (await AssistantTestSetup.CountConversationsAsync(
            fixture, userId, TestContext.Current.CancellationToken))
            .ShouldBe(conversationsBefore);
        (await AssistantTestSetup.CountAuditEventsAsync(
            fixture, "provider_error", TestContext.Current.CancellationToken))
            .ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Probe_Requires_Flag_And_Valid_Scenario()
    {
        await using var noProbeFactory = AssistantTestSetup.CreateFactory(fixture, probesEnabled: false);
        using var noProbeClient = noProbeFactory.CreateClient();
        var token = await AssistantTestSetup.AssistantUserAsync(noProbeClient, noProbeFactory);
        using var missing = await noProbeClient.SendAsync(
            ComplianceTestUsers.Post("/api/v1/diagnostics/assistant-scenario", new { scenario = "error" })
                .Auth(token),
            TestContext.Current.CancellationToken);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var invalid = await _client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/diagnostics/assistant-scenario", new { scenario = "bogus" })
                .Auth(token),
            TestContext.Current.CancellationToken);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var invalidEnvelope = await EnvelopeAsync(invalid);
        ComplianceTestUsers.ErrorCode(invalidEnvelope).ShouldBe("validation.failed");

        var eligible = await EligibleUserAsync();
        await ProbeAssistantAsync(eligible, "advice_phrase");
        try
        {
            var data = await AssistantTestSetup.SendAsync(_client, eligible, "What is a tokenized stock?");
            data.GetProperty("policy").GetProperty("blocked").GetBoolean().ShouldBeTrue();
            data.GetProperty("text").GetString()
                .ShouldBe(_factory.Services.GetRequiredService<
                    Microsoft.Extensions.Options.IOptions<AskVest.Modules.Assistant.Configuration.AssistantOptions>>()
                    .Value.SafeFallbackMessage);
        }
        finally
        {
            await ProbeAssistantAsync(eligible, "normal");
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Compliance.Providers;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Ledger;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LedgerApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string WebhookSecret = "test-kyc-webhook-secret-0123456789abcdef";

    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = ComplianceTestUsers.CreateFactory(fixture);
        _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<string> PracticeUserAsync(HttpClient client, AuthApiFactory factory)
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(factory);
        await ComplianceTestUsers.AcceptDocumentsAsync(
            client,
            token,
            ("terms", "v1", new string('a', 64)),
            ("privacy", "v1", new string('b', 64)));
        await ComplianceTestUsers.SetCountryAsync(client, token, "NG");
        return token;
    }

    private static async Task<string> ApprovedRealUserAsync(HttpClient client, AuthApiFactory factory)
    {
        var token = await PracticeUserAsync(client, factory);
        await ComplianceTestUsers.AcceptDocumentsAsync(
            client,
            token,
            ("risk_disclosure", "v1", new string('c', 64)),
            ("token_disclosure", "v1", new string('d', 64)));

        using var start = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/kyc/start", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reference = (await ComplianceTestUsers.DataAsync(start))
            .GetProperty("providerReference").GetString()!;

        var body = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["eventId"] = $"evt-{Guid.CreateVersion7():N}",
            ["providerReference"] = reference,
            ["outcome"] = "approved",
            ["verifiedCountry"] = "NG",
            ["fullName"] = "Amara Okafor",
            ["dateOfBirth"] = "1990-04-12",
        });
        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString();
        using var webhook = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/webhooks/kyc/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        webhook.Headers.Add("X-Webhook-Timestamp", timestamp);
        webhook.Headers.Add("X-Webhook-Signature", FakeKycProvider.Signature(timestamp, body, WebhookSecret));
        using var webhookResponse = await client.SendAsync(webhook, TestContext.Current.CancellationToken);
        webhookResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        return token;
    }

    private static HttpRequestMessage Probe(string book, string kind, string asset, string amount, string? key = null)
        => ComplianceTestUsers.Post($"/api/v1/{book}/diagnostics/ledger-post", new { kind, asset, amount }, key);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.DataAsync(response);

    private static string Amount(JsonElement item) => item.GetProperty("amount").GetString()!;

    [Fact]
    public async Task Unauthenticated_Requests_Get_401()
    {
        var client = Client();
        foreach (var request in new[]
        {
            ComplianceTestUsers.Get("/api/v1/practice/balances"),
            ComplianceTestUsers.Get("/api/v1/real/balances"),
            ComplianceTestUsers.Get("/api/v1/practice/ledger/entries"),
            Probe("practice", "credit", "USD", "1"),
            Probe("real", "credit", "USD", "1"),
        })
        {
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Ineligible_User_Gets_403_With_Not_Eligible()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);

        using var real = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/balances").Auth(token),
            TestContext.Current.CancellationToken);
        real.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ComplianceTestUsers.ErrorCode(await ComplianceTestUsers.EnvelopeAsync(real))
            .ShouldBe("compliance.not_eligible");

        using var practice = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/balances").Auth(token),
            TestContext.Current.CancellationToken);
        practice.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Probe_Credit_And_Debit_Work_And_Overdraft_Returns_Envelope_Error()
    {
        var client = Client();
        var token = await PracticeUserAsync(client, _factory);

        using var credit = await client.SendAsync(
            Probe("practice", "credit", "USD", "100").Auth(token),
            TestContext.Current.CancellationToken);
        credit.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var debit = await client.SendAsync(
            Probe("practice", "debit", "USD", "30").Auth(token),
            TestContext.Current.CancellationToken);
        debit.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var balances = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/balances").Auth(token),
            TestContext.Current.CancellationToken);
        balances.StatusCode.ShouldBe(HttpStatusCode.OK);
        var balanceItems = (await DataAsync(balances)).EnumerateArray().ToList();
        balanceItems.ShouldHaveSingleItem();
        balanceItems[0].GetProperty("available").GetString().ShouldBe("70");
        balanceItems[0].GetProperty("available").ValueKind.ShouldBe(JsonValueKind.String);

        using var overdraft = await client.SendAsync(
            Probe("practice", "debit", "USD", "100").Auth(token),
            TestContext.Current.CancellationToken);
        overdraft.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var envelope = await ComplianceTestUsers.EnvelopeAsync(overdraft);
        ComplianceTestUsers.ErrorCode(envelope).ShouldBe("ledger.insufficient_funds");

        using var after = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/balances").Auth(token),
            TestContext.Current.CancellationToken);
        var afterItems = (await DataAsync(after)).EnumerateArray().ToList();
        afterItems[0].GetProperty("available").GetString().ShouldBe("70");
    }

    [Fact]
    public async Task Probe_Idempotency_Replay_Returns_Identical_Response()
    {
        var client = Client();
        var token = await PracticeUserAsync(client, _factory);
        const string key = "probe-replay-key-0001";

        using var first = await client.SendAsync(
            Probe("practice", "credit", "USD", "25", key).Auth(token),
            TestContext.Current.CancellationToken);
        using var replay = await client.SendAsync(
            Probe("practice", "credit", "USD", "25", key).Auth(token),
            TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstData = await DataAsync(first);
        var replayData = await DataAsync(replay);
        firstData.GetProperty("journalId").GetGuid().ShouldBe(replayData.GetProperty("journalId").GetGuid());

        using var balances = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/balances").Auth(token),
            TestContext.Current.CancellationToken);
        var items = (await DataAsync(balances)).EnumerateArray().ToList();
        items[0].GetProperty("available").GetString().ShouldBe("25");
    }

    [Fact]
    public async Task Same_Idempotency_Key_From_Two_Users_Never_Collides()
    {
        var client = Client();
        var tokenA = await PracticeUserAsync(client, _factory);
        var tokenB = await PracticeUserAsync(client, _factory);
        const string key = "shared-across-users-1";

        using var first = await client.SendAsync(
            Probe("practice", "credit", "USD", "5", key).Auth(tokenA),
            TestContext.Current.CancellationToken);
        using var second = await client.SendAsync(
            Probe("practice", "credit", "USD", "7", key).Auth(tokenB),
            TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstData = await DataAsync(first);
        var secondData = await DataAsync(second);
        firstData.GetProperty("journalId").GetGuid().ShouldNotBe(secondData.GetProperty("journalId").GetGuid());

        foreach (var (token, expected) in new[] { (tokenA, "5"), (tokenB, "7") })
        {
            using var balances = await client.SendAsync(
                ComplianceTestUsers.Get("/api/v1/practice/balances").Auth(token),
                TestContext.Current.CancellationToken);
            var items = (await DataAsync(balances)).EnumerateArray().ToList();
            items[0].GetProperty("available").GetString().ShouldBe(expected);
        }
    }

    [Fact]
    public async Task Real_Routes_Gate_Until_Kyc_Approval_Then_Stay_Separate()
    {
        var client = Client();
        var practiceToken = await PracticeUserAsync(client, _factory);

        using var denied = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/balances").Auth(practiceToken),
            TestContext.Current.CancellationToken);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var realToken = await ApprovedRealUserAsync(client, _factory);

        using var credit = await client.SendAsync(
            Probe("real", "credit", "USD", "100").Auth(realToken),
            TestContext.Current.CancellationToken);
        credit.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var practiceCredit = await client.SendAsync(
            Probe("practice", "credit", "USD", "40").Auth(realToken),
            TestContext.Current.CancellationToken);
        practiceCredit.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var realBalances = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/balances").Auth(realToken),
            TestContext.Current.CancellationToken);
        var realItems = (await DataAsync(realBalances)).EnumerateArray().ToList();
        realItems.ShouldHaveSingleItem();
        realItems[0].GetProperty("available").GetString().ShouldBe("100");

        using var practiceBalances = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/balances").Auth(realToken),
            TestContext.Current.CancellationToken);
        var practiceItems = (await DataAsync(practiceBalances)).EnumerateArray().ToList();
        practiceItems.ShouldHaveSingleItem();
        practiceItems[0].GetProperty("available").GetString().ShouldBe("40");

        using var realEntries = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/ledger/entries").Auth(realToken),
            TestContext.Current.CancellationToken);
        var realEntryItems = (await DataAsync(realEntries)).GetProperty("items").EnumerateArray().ToList();
        realEntryItems.ShouldHaveSingleItem();
        Amount(realEntryItems[0]).ShouldBe("100");

        using var practiceEntries = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/ledger/entries").Auth(realToken),
            TestContext.Current.CancellationToken);
        var practiceEntryItems = (await DataAsync(practiceEntries)).GetProperty("items").EnumerateArray().ToList();
        practiceEntryItems.ShouldHaveSingleItem();
        Amount(practiceEntryItems[0]).ShouldBe("40");
    }

    [Fact]
    public async Task History_Paginates_With_Cursor_And_Owners_See_Only_Their_Entries()
    {
        var client = Client();
        var tokenA = await PracticeUserAsync(client, _factory);
        var tokenB = await PracticeUserAsync(client, _factory);

        for (var i = 0; i < 5; i++)
        {
            using var credit = await client.SendAsync(
                Probe("practice", "credit", "USD", "1").Auth(tokenA),
                TestContext.Current.CancellationToken);
            credit.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        while (pages < 10)
        {
            var path = "/api/v1/practice/ledger/entries?limit=2" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var response = await client.SendAsync(
                ComplianceTestUsers.Get(path).Auth(tokenA),
                TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var data = await DataAsync(response);
            foreach (var item in data.GetProperty("items").EnumerateArray())
            {
                seen.Add(item.GetProperty("entryId").GetGuid().ToString());
                Amount(item).ShouldBe("1");
                item.GetProperty("asset").GetString().ShouldBe("USD");
                item.GetProperty("amount").ValueKind.ShouldBe(JsonValueKind.String);
            }

            pages++;
            if (!data.GetProperty("hasMore").GetBoolean())
            {
                break;
            }

            cursor = data.GetProperty("nextCursor").GetString();
        }

        pages.ShouldBe(3);
        seen.Distinct().Count().ShouldBe(5);

        using var other = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/ledger/entries").Auth(tokenB),
            TestContext.Current.CancellationToken);
        other.StatusCode.ShouldBe(HttpStatusCode.OK);
        var otherItems = (await DataAsync(other)).GetProperty("items").EnumerateArray().ToList();
        otherItems.ShouldBeEmpty();
    }

    [Fact]
    public async Task Probes_Are_Absent_When_The_Flag_Is_Off()
    {
        await using var factory = ComplianceTestUsers.CreateFactory(
            fixture,
            ComplianceTestUsers.Settings(probesEnabled: false));
        factory.CreateClient();
        var client = factory.CreateClient();
        var token = await PracticeUserAsync(client, factory);

        using var response = await client.SendAsync(
            Probe("practice", "credit", "USD", "1").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

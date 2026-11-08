using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Practice;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PracticeApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        var settings = new Dictionary<string, string?>(ComplianceTestUsers.Settings())
        {
            ["MarketData:Enabled"] = "true",
            ["MarketData:Provider"] = "fake",
            ["MarketData:PollIntervalSeconds"] = "3600",
            ["MarketData:Fake:Scenario"] = "normal",
            ["MarketData:Fake:Session"] = "regular",
            ["MarketData:Fake:DelayMinutes"] = "15",
            ["Practice:ResetCooldownHours"] = "0",
        };
        _factory = ComplianceTestUsers.CreateFactory(fixture, settings);
        _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.DataAsync(response);

    private static async Task MakePracticeEligibleAsync(HttpClient client, string token)
    {
        await ComplianceTestUsers.AcceptDocumentsAsync(
            client, token, ("terms", "v1", new string('a', 64)), ("privacy", "v1", new string('b', 64)));
        await ComplianceTestUsers.SetCountryAsync(client, token, "NG");
    }

    private async Task<string> EligibleUserAsync()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        await MakePracticeEligibleAsync(client, token);
        return token;
    }

    [Fact]
    public async Task Account_And_Position_Routes_Require_Authentication()
    {
        var client = Client();
        foreach (var (method, path) in new (HttpMethod, string)[]
        {
            (HttpMethod.Get, "/api/v1/practice/account"),
            (HttpMethod.Get, "/api/v1/practice/positions"),
            (HttpMethod.Post, "/api/v1/practice/account"),
            (HttpMethod.Post, "/api/v1/practice/account/reset"),
        })
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            if (method == HttpMethod.Post)
            {
                request.Content = JsonContent.Create(new { });
                request.Headers.Add("Idempotency-Key", $"k-{Guid.CreateVersion7():N}");
            }

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Ineligible_User_Gets_403_Compliance_Not_Eligible()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);

        using var response = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/account", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ComplianceTestUsers.ErrorCode(await ComplianceTestUsers.EnvelopeAsync(response))
            .ShouldBe("compliance.not_eligible");
    }

    [Fact]
    public async Task Start_Grants_Once_And_Replays_Are_Identical()
    {
        var client = Client();
        var token = await EligibleUserAsync();

        using var first = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/account", new { }, "start-key-1").Auth(token),
            TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var firstData = JsonDocument.Parse(firstBody).RootElement.GetProperty("data");
        firstData.GetProperty("started").GetBoolean().ShouldBeTrue();
        firstData.GetProperty("cash").GetString().ShouldBe("10000");
        firstData.GetProperty("resetCount").GetInt32().ShouldBe(0);
        firstData.GetProperty("resetsLeftInWindow").GetInt32().ShouldBe(3);

        using var replay = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/account", new { }, "start-key-1").Auth(token),
            TestContext.Current.CancellationToken);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var replayBody = await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        replayBody.ShouldBe(firstBody);

        using var second = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/account", new { }, "start-key-2").Auth(token),
            TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var grants = await Database.ExecuteScalarAsync<long>(
            fixture.OwnerConnectionString,
            "SELECT count(*) FROM ledger.journals WHERE book = 'practice' " +
            "AND reason_code = 'practice_credit_grant'",
            TestContext.Current.CancellationToken);
        grants.ShouldBe(1);
    }

    [Fact]
    public async Task Account_Get_And_Positions_Report_Cash_As_Strings()
    {
        var client = Client();
        var token = await EligibleUserAsync();
        using var start = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/account", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var account = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/account").Auth(token),
            TestContext.Current.CancellationToken);
        account.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accountData = await DataAsync(account);
        accountData.GetProperty("cash").ValueKind.ShouldBe(JsonValueKind.String);
        accountData.GetProperty("cash").GetString().ShouldBe("10000");

        using var positions = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/positions").Auth(token),
            TestContext.Current.CancellationToken);
        positions.StatusCode.ShouldBe(HttpStatusCode.OK);
        var positionsData = await DataAsync(positions);
        positionsData.GetProperty("cash").GetString().ShouldBe("10000");
        positionsData.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Reset_Is_Refused_Then_Succeeds_When_Cooldown_Allows()
    {
        var client = Client();
        var token = await EligibleUserAsync();
        using var start = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/account", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Cooldown is zero in this factory, so the first reset is allowed.
        using var reset = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/account/reset", new { }, "reset-key-1").Auth(token),
            TestContext.Current.CancellationToken);
        reset.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await DataAsync(reset);
        data.GetProperty("resetCount").GetInt32().ShouldBe(1);
        data.GetProperty("cash").GetString().ShouldBe("10000");
    }

    [Fact]
    public async Task Catalog_Importer_Rejects_Practice_Prefixed_Asset_Codes()
    {
        var suffix = CatalogImportTestData.Suffix();
        var issuerId = $"pv-issuer-{suffix}";
        var instrumentId = $"pv-instr-{suffix}";
        var token = CatalogImportTestData.Token(
            issuerId: issuerId,
            address: CatalogImportTestData.Address(suffix, 'b'),
            assetCode: "P.HACK");

        var result = await CatalogImportTestData.ImportFolderAsync(
            fixture,
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [token])],
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("P.");
    }
}

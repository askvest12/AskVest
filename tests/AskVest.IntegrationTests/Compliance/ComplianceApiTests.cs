using System.Net;
using System.Text.Json;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Compliance;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class ComplianceApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = ComplianceTestUsers.CreateFactory(fixture);
        _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<JsonElement> DocumentListAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/compliance/documents").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ComplianceTestUsers.DataAsync(response);
    }

    private static object ConsentBody(params (string Type, string Version, string Sha256)[] docs)
        => new { documents = docs.Select(d => new { type = d.Type, version = d.Version, sha256 = d.Sha256 }) };

    private static async Task<T> ScalarAsync<T>(PostgresFixture fixture, string sql, params (string Name, object Value)[] args)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<List<Dictionary<string, object?>>> QueryAsync(PostgresFixture fixture, string sql, params (string Name, object Value)[] args)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, TestContext.Current.CancellationToken)
                    ? null
                    : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    [Fact]
    public async Task Documents_Reflect_Configuration_And_Per_User_Acceptance()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);

        var documents = await DocumentListAsync(client, token);
        var items = documents.GetProperty("documents").EnumerateArray().ToList();
        items.Count.ShouldBe(4);
        items.ShouldAllBe(d => d.GetProperty("accepted").GetBoolean() == false);
        items.Select(d => d.GetProperty("type").GetString()).ShouldBe(
            ["terms", "privacy", "risk_disclosure", "token_disclosure"], ignoreOrder: true);

        var terms = items.Single(d => d.GetProperty("type").GetString() == "terms");
        using var accept = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/consents", ConsentBody(
                ("terms", terms.GetProperty("version").GetString()!, terms.GetProperty("sha256").GetString()!),
                ("privacy", "v1", new string('b', 64)))).Auth(token),
            TestContext.Current.CancellationToken);
        accept.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await DocumentListAsync(client, token);
        var afterItems = after.GetProperty("documents").EnumerateArray().ToList();
        afterItems.Single(d => d.GetProperty("type").GetString() == "terms")
            .GetProperty("accepted").GetBoolean().ShouldBeTrue();
        afterItems.Single(d => d.GetProperty("type").GetString() == "privacy")
            .GetProperty("accepted").GetBoolean().ShouldBeTrue();
        afterItems.Single(d => d.GetProperty("type").GetString() == "risk_disclosure")
            .GetProperty("accepted").GetBoolean().ShouldBeFalse();
    }

    [Theory]
    [InlineData("wrong_version")]
    [InlineData("wrong_hash")]
    [InlineData("unknown_type")]
    [InlineData("partial")]
    public async Task Consent_Mismatches_Return_409_And_Record_Nothing(string mode)
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var userId = UserIdForToken(token);

        var body = mode switch
        {
            "wrong_version" => ConsentBody(("terms", "vX", new string('a', 64))),
            "wrong_hash" => ConsentBody(("terms", "v1", new string('e', 64))),
            "unknown_type" => ConsentBody(("visa", "v1", new string('a', 64))),
            _ => ConsentBody(("terms", "v1", new string('a', 64)), ("privacy", "vX", new string('b', 64))),
        };

        using var response = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/consents", body).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        ComplianceTestUsers.ErrorCode(await ComplianceTestUsers.EnvelopeAsync(response))
            .ShouldBe("compliance.consent_version_mismatch");

        var count = await ScalarAsync<long>(
            fixture,
            "SELECT count(*) FROM compliance.consent_records WHERE user_id = @u",
            ("u", userId));
        count.ShouldBe(0);
    }

    [Fact]
    public async Task Correct_Consent_Records_Once_And_Repeats_Are_Harmless()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var userId = UserIdForToken(token);
        var body = ConsentBody(
            ("terms", "v1", new string('a', 64)),
            ("privacy", "v1", new string('b', 64)));

        using var first = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/consents", body).Auth(token),
            TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var second = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/consents", body).Auth(token),
            TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var count = await ScalarAsync<long>(
            fixture,
            "SELECT count(*) FROM compliance.consent_records WHERE user_id = @u",
            ("u", userId));
        count.ShouldBe(2);
    }

    [Fact]
    public async Task Same_Idempotency_Key_Replays_The_Same_Response()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var userId = UserIdForToken(token);
        var key = $"idem-{Guid.CreateVersion7():N}";
        var body = ConsentBody(("terms", "v1", new string('a', 64)));

        using var first = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/consents", body, key).Auth(token),
            TestContext.Current.CancellationToken);
        var firstBody = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var replay = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/consents", body, key).Auth(token),
            TestContext.Current.CancellationToken);
        var replayBody = await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        replayBody.ShouldBe(firstBody);

        var count = await ScalarAsync<long>(
            fixture,
            "SELECT count(*) FROM compliance.consent_records WHERE user_id = @u",
            ("u", userId));
        count.ShouldBe(1);
    }

    [Fact]
    public async Task Raising_Document_Version_Requires_Acceptance_Again()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        using var accept = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/consents",
                ConsentBody(("terms", "v1", new string('a', 64)))).Auth(token),
            TestContext.Current.CancellationToken);
        accept.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var bumped = new AuthApiFactory(
            fixture.AppConnectionString,
            ComplianceTestUsers.Settings(documentVersion: "v2"));
        try
        {
            var bumpedClient = bumped.CreateClient();
            using var documents = await bumpedClient.SendAsync(
                ComplianceTestUsers.Get("/api/v1/compliance/documents").Auth(token),
                TestContext.Current.CancellationToken);
            var data = await ComplianceTestUsers.DataAsync(documents);
            data.GetProperty("documents").EnumerateArray()
                .Single(d => d.GetProperty("type").GetString() == "terms")
                .GetProperty("accepted").GetBoolean().ShouldBeFalse();

            using var eligibility = await bumpedClient.SendAsync(
                ComplianceTestUsers.Get("/api/v1/compliance/eligibility").Auth(token),
                TestContext.Current.CancellationToken);
            var eligibilityData = await ComplianceTestUsers.DataAsync(eligibility);
            var practiceCodes = eligibilityData.GetProperty("blockers").GetProperty("practice")
                .EnumerateArray().Select(b => b.GetProperty("code").GetString()).ToList();
            practiceCodes.ShouldContain("consent_required");
        }
        finally
        {
            await bumped.DisposeAsync();
        }
    }

    [Fact]
    public async Task Invalid_Country_Is_Rejected()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        using var response = await client.SendAsync(
            ComplianceTestUsers.Put("/api/v1/compliance/country", new { country = "XX1" }).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Blocked_Country_Is_Accepted_But_Reports_Country_Not_Supported()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        using var put = await client.SendAsync(
            ComplianceTestUsers.Put("/api/v1/compliance/country", new { country = "KP" }).Auth(token),
            TestContext.Current.CancellationToken);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var eligibility = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/compliance/eligibility").Auth(token),
            TestContext.Current.CancellationToken);
        var data = await ComplianceTestUsers.DataAsync(eligibility);
        var codes = data.GetProperty("blockers").GetProperty("practice")
            .EnumerateArray().Select(b => b.GetProperty("code").GetString()).ToList();
        codes.ShouldContain("country_not_supported");
    }

    [Fact]
    public async Task Country_Change_Writes_Events_And_Is_Refused_After_Kyc_Approval()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var userId = UserIdForToken(token);

        using var first = await client.SendAsync(
            ComplianceTestUsers.Put("/api/v1/compliance/country", new { country = "NG" }).Auth(token),
            TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var second = await client.SendAsync(
            ComplianceTestUsers.Put("/api/v1/compliance/country", new { country = "US" }).Auth(token),
            TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var events = await QueryAsync(
            fixture,
            "SELECT event_type FROM compliance.compliance_events WHERE user_id = @u ORDER BY occurred_at",
            ("u", userId));
        events.Select(e => e["event_type"]!.ToString()).ShouldBe(
            ["CountrySet", "CountryChanged"]);

        await ExecuteSqlAsync(
            fixture,
            """
            INSERT INTO compliance.compliance_profiles
                (user_id, declared_country, kyc_status, kyc_provider, attempts_used,
                 screening_status, created_at, updated_at)
            VALUES (@u, 'US', 'Approved', 'fake', 1, 'Clear', now(), now())
            ON CONFLICT (user_id) DO UPDATE SET kyc_status = 'Approved'
            """,
            ("u", userId));

        using var refused = await client.SendAsync(
            ComplianceTestUsers.Put("/api/v1/compliance/country", new { country = "GB" }).Auth(token),
            TestContext.Current.CancellationToken);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Eligibility_Endpoint_Returns_Flags_Kyc_Status_And_Blockers()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        using var response = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/compliance/eligibility").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await ComplianceTestUsers.DataAsync(response);
        data.GetProperty("practiceAllowed").GetBoolean().ShouldBeFalse();
        data.GetProperty("realAllowed").GetBoolean().ShouldBeFalse();
        data.GetProperty("kycStatus").GetString().ShouldBe("NotStarted");
        var practiceCodes = data.GetProperty("blockers").GetProperty("practice")
            .EnumerateArray().Select(b => b.GetProperty("code").GetString()).ToList();
        practiceCodes.ShouldContain("country_required");
        practiceCodes.ShouldContain("consent_required");
    }

    private static Guid UserIdForToken(string token)
    {
        var payload = token.Split('.')[1];
        var json = JsonDocument.Parse(Convert.FromBase64String(Pad(payload))).RootElement;
        return json.GetProperty("sub").GetGuid();
    }

    private static string Pad(string value)
        => value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);

    private static async Task ExecuteSqlAsync(PostgresFixture fixture, string sql, params (string Name, object Value)[] args)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

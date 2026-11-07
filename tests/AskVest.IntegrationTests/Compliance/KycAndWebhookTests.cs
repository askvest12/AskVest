using System.Net;
using System.Text;
using System.Text.Json;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Compliance.Providers;
using AskVest.Modules.Compliance.Services;
using AskVest.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Compliance;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class KycAndWebhookTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static async Task<string> RealReadyUserAsync(HttpClient client, AuthApiFactory factory)
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(factory);
        await ComplianceTestUsers.AcceptDocumentsAsync(client, token, ComplianceTestUsers.AllDocuments);
        await ComplianceTestUsers.SetCountryAsync(client, token, "NG");
        return token;
    }

    private static async Task<JsonElement> StartKycAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/kyc/start", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ComplianceTestUsers.DataAsync(response);
    }

    private static string WebhookBody(
        string eventId,
        string providerReference,
        string outcome,
        string? rejectionCode = null,
        string verifiedCountry = "NG",
        string fullName = "Amara Okafor",
        string dateOfBirth = "1990-04-12")
        => JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["eventId"] = eventId,
            ["providerReference"] = providerReference,
            ["outcome"] = outcome,
            ["rejectionCode"] = rejectionCode,
            ["verifiedCountry"] = verifiedCountry,
            ["fullName"] = fullName,
            ["dateOfBirth"] = dateOfBirth,
        });

    private static HttpRequestMessage SignedWebhook(string body, string? timestampOverride = null, bool badSignature = false)
    {
        var timestamp = timestampOverride
            ?? TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString();
        var signature = badSignature
            ? new string('0', 64)
            : FakeKycProvider.Signature(timestamp, body, WebhookSecret);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/webhooks/kyc/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Webhook-Timestamp", timestamp);
        request.Headers.Add("X-Webhook-Signature", signature);
        return request;
    }

    private static async Task<JsonElement> EligibilityAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/compliance/eligibility").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ComplianceTestUsers.DataAsync(response);
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args)
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

    [Fact]
    public async Task Kyc_Start_Refused_Until_Practice_Real_Consents_And_Country_Hold()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);

        using var noPractice = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/kyc/start", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        noPractice.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await ComplianceTestUsers.AcceptDocumentsAsync(
            client, token, ComplianceTestUsers.AllDocuments.Take(2).ToArray());
        await ComplianceTestUsers.SetCountryAsync(client, token, "NG");

        using var noRealConsents = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/kyc/start", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        noRealConsents.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var envelope = await ComplianceTestUsers.EnvelopeAsync(noRealConsents);
        ComplianceTestUsers.ErrorCode(envelope).ShouldBe("compliance.not_eligible");
        ComplianceTestUsers.ErrorDetailFields(envelope).ShouldContain("consent_required");
    }

    [Fact]
    public async Task Kyc_Start_Refused_When_Country_Is_Not_Real_Money_Enabled()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client, _factory);
        await ComplianceTestUsers.SetCountryAsync(client, token, "GH");

        using var response = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/kyc/start", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var envelope = await ComplianceTestUsers.EnvelopeAsync(response);
        ComplianceTestUsers.ErrorDetailFields(envelope).ShouldContain("country_not_supported");
    }

    [Fact]
    public async Task Kyc_Start_Resumes_In_Review_And_Exhausts_After_Three_Attempts()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client, _factory);

        var first = await StartKycAsync(client, token);
        var reference = first.GetProperty("providerReference").GetString()!;
        first.GetProperty("sessionUrl").GetString()!.ShouldContain(reference);

        var second = await StartKycAsync(client, token);
        second.GetProperty("providerReference").GetString().ShouldBe(reference);

        var attempts = await ScalarAsync<int>(
            "SELECT attempts_used FROM compliance.compliance_profiles WHERE provider_reference = @r",
            ("r", reference));
        attempts.ShouldBe(1);

        var rejection = WebhookBody("evt-reject-1", reference, "rejected", rejectionCode: "document_unreadable");
        using var firstRejection = await client.SendAsync(
            SignedWebhook(rejection),
            TestContext.Current.CancellationToken);
        firstRejection.StatusCode.ShouldBe(HttpStatusCode.OK);

        for (var attempt = 2; attempt <= 3; attempt++)
        {
            var nextReference = (await StartKycAsync(client, token))
                .GetProperty("providerReference").GetString()!;
            var cycleRejection = WebhookBody(
                $"evt-reject-{attempt}",
                nextReference,
                "rejected",
                rejectionCode: "document_unreadable");
            using var rejected = await client.SendAsync(
                SignedWebhook(cycleRejection),
                TestContext.Current.CancellationToken);
            rejected.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var exhausted = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/kyc/start", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        exhausted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ComplianceTestUsers.EnvelopeAsync(exhausted))
            .GetProperty("error").GetProperty("code").GetString()
            .ShouldBe("compliance.kyc_attempts_exhausted");
    }

    [Fact]
    public async Task Kyc_Start_Provider_Failure_Changes_Nothing()
    {
        await using var factory = ComplianceTestUsers.CreateFactory(
            fixture,
            configureServices: services => services.Replace(
                ServiceDescriptor.Singleton<IKycProvider>(new FailingKycProvider())));
        var client = factory.CreateClient();
        var token = await RealReadyUserAsync(client, factory);

        using var response = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/kyc/start", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var status = await ScalarAsync<string>(
            "SELECT kyc_status FROM compliance.compliance_profiles WHERE declared_country = 'NG' ORDER BY created_at DESC LIMIT 1");
        status.ShouldBe("NotStarted");
    }

    [Fact]
    public async Task Webhook_Approval_Makes_Real_Eligible_And_Stores_Decision()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client, _factory);
        var reference = (await StartKycAsync(client, token)).GetProperty("providerReference").GetString()!;

        var body = WebhookBody("evt-approve-1", reference, "approved");
        using var webhook = await client.SendAsync(SignedWebhook(body), TestContext.Current.CancellationToken);
        webhook.StatusCode.ShouldBe(HttpStatusCode.OK);

        var eligibility = await EligibilityAsync(client, token);
        eligibility.GetProperty("realAllowed").GetBoolean().ShouldBeTrue();
        eligibility.GetProperty("kycStatus").GetString().ShouldBe("Approved");

        using var realProbe = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/diagnostics/ping").Auth(token),
            TestContext.Current.CancellationToken);
        realProbe.StatusCode.ShouldBe(HttpStatusCode.OK);

        var profile = await ScalarAsync<string>(
            "SELECT kyc_status || '|' || verified_country || '|' || screening_status FROM compliance.compliance_profiles WHERE provider_reference = @r",
            ("r", reference));
        profile.ShouldBe("Approved|NG|Clear");

        var events = await ScalarAsync<long>(
            """
            SELECT count(*) FROM compliance.compliance_events e
            JOIN compliance.compliance_profiles p ON p.user_id = e.user_id
            WHERE p.provider_reference = @r AND e.event_type IN ('KycApproved', 'ScreeningClear')
            """,
            ("r", reference));
        events.ShouldBe(2);
    }

    [Fact]
    public async Task Webhook_Rejection_Allows_A_Retry()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client, _factory);
        var reference = (await StartKycAsync(client, token)).GetProperty("providerReference").GetString()!;

        var body = WebhookBody("evt-reject-retry-1", reference, "rejected", rejectionCode: "blurry_document");
        using var webhook = await client.SendAsync(SignedWebhook(body), TestContext.Current.CancellationToken);
        webhook.StatusCode.ShouldBe(HttpStatusCode.OK);

        var eligibility = await EligibilityAsync(client, token);
        eligibility.GetProperty("kycStatus").GetString().ShouldBe("Rejected");
        eligibility.GetProperty("realAllowed").GetBoolean().ShouldBeFalse();

        var retry = await StartKycAsync(client, token);
        retry.GetProperty("providerReference").GetString().ShouldNotBe(reference);
    }

    [Theory]
    [InlineData("bad_signature")]
    [InlineData("stale_timestamp")]
    [InlineData("missing_timestamp")]
    public async Task Webhook_Rejects_Bad_Credentials(string mode)
    {
        var client = Client();
        var body = WebhookBody("evt-bad-1", "fake-unknown", "approved");
        HttpRequestMessage request;
        HttpStatusCode expected;
        if (mode == "bad_signature")
        {
            request = SignedWebhook(body, badSignature: true);
            expected = HttpStatusCode.Unauthorized;
        }
        else if (mode == "stale_timestamp")
        {
            var stale = (TimeProvider.System.GetUtcNow().AddMinutes(-30)).ToUnixTimeSeconds().ToString();
            request = SignedWebhook(body, timestampOverride: stale);
            expected = HttpStatusCode.BadRequest;
        }
        else
        {
            request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/webhooks/kyc/fake", UriKind.Relative))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Webhook-Signature", FakeKycProvider.Signature("1", body, WebhookSecret));
            expected = HttpStatusCode.BadRequest;
        }

        using (request)
        {
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(expected);
            var code = (await ComplianceTestUsers.EnvelopeAsync(response))
                .GetProperty("error").GetProperty("code").GetString();
            code.ShouldBe(mode == "bad_signature" ? "webhook.invalid_signature" : "webhook.stale_timestamp");
        }

        if (mode == "bad_signature")
        {
            var rejected = await ScalarAsync<long>(
                "SELECT count(*) FROM compliance.compliance_events WHERE event_type = 'WebhookRejected'");
            rejected.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Webhook_Duplicate_Event_Id_Returns_200_Without_Change()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client, _factory);
        var reference = (await StartKycAsync(client, token)).GetProperty("providerReference").GetString()!;
        var body = WebhookBody("evt-dupe-1", reference, "approved");

        using var first = await client.SendAsync(SignedWebhook(body), TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var second = await client.SendAsync(SignedWebhook(body), TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var webhookRows = await ScalarAsync<long>(
            "SELECT count(*) FROM compliance.kyc_webhook_events WHERE event_id = 'evt-dupe-1'");
        webhookRows.ShouldBe(1);
        var approvedEvents = await ScalarAsync<long>(
            """
            SELECT count(*) FROM compliance.compliance_events e
            JOIN compliance.compliance_profiles p ON p.user_id = e.user_id
            WHERE p.provider_reference = @r AND e.event_type = 'KycApproved'
            """,
            ("r", reference));
        approvedEvents.ShouldBe(1);
    }

    [Fact]
    public async Task Webhook_Unknown_Provider_404_And_Unknown_Reference_200()
    {
        var client = Client();
        var body = WebhookBody("evt-x-1", "fake-ref", "approved");

        using var unknownProviderRequest = SignedWebhook(body);
        unknownProviderRequest.RequestUri = new Uri("/api/v1/webhooks/kyc/other", UriKind.Relative);
        using var unknownProvider = await client.SendAsync(
            unknownProviderRequest,
            TestContext.Current.CancellationToken);
        unknownProvider.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var token = await RealReadyUserAsync(client, _factory);
        var reference = (await StartKycAsync(client, token)).GetProperty("providerReference").GetString()!;
        var unmatched = WebhookBody("evt-x-2", "fake-no-such-reference", "rejected", rejectionCode: "x");
        using var unmatchedResponse = await client.SendAsync(
            SignedWebhook(unmatched),
            TestContext.Current.CancellationToken);
        unmatchedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var eligibility = await EligibilityAsync(client, token);
        eligibility.GetProperty("kycStatus").GetString().ShouldBe("InReview");
    }

    [Fact]
    public async Task Webhook_Oversized_Body_Returns_413()
    {
        var client = Client();
        var body = "{\"pad\":\"" + new string('x', KycWebhookService.MaxBodyBytes) + "\"}";
        using var response = await client.SendAsync(
            SignedWebhook(body),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        var code = (await ComplianceTestUsers.EnvelopeAsync(response))
            .GetProperty("error").GetProperty("code").GetString();
        code.ShouldBe("request.too_large");
    }

    [Fact]
    public async Task Webhook_Screening_Hit_Restricts_Real()
    {
        var markerSettings = new Dictionary<string, string?>(ComplianceTestUsers.Settings())
        {
            ["Compliance:Screening:HitNameMarker"] = "sanctioned",
        };
        await using var markerFactory = ComplianceTestUsers.CreateFactory(fixture, markerSettings);
        var client = markerFactory.CreateClient();
        var token = await RealReadyUserAsync(client, markerFactory);
        var reference = (await StartKycAsync(client, token)).GetProperty("providerReference").GetString()!;

        var body = WebhookBody("evt-hit-1", reference, "approved", fullName: "Sanctioned Person");
        using var webhook = await client.SendAsync(SignedWebhook(body), TestContext.Current.CancellationToken);
        webhook.StatusCode.ShouldBe(HttpStatusCode.OK);

        var eligibility = await EligibilityAsync(client, token);
        eligibility.GetProperty("realAllowed").GetBoolean().ShouldBeFalse();
        var realBlockers = eligibility.GetProperty("blockers").GetProperty("real")
            .EnumerateArray().Select(b => b.GetProperty("code").GetString()).ToList();
        realBlockers.ShouldContain("account_restricted");
    }

    [Fact]
    public async Task Webhook_Screening_Failure_Rolls_Back_And_Retry_Succeeds()
    {
        await using var factory = ComplianceTestUsers.CreateFactory(
            fixture,
            configureServices: services => services.Replace(
                ServiceDescriptor.Singleton<IScreeningProvider>(new FailingScreeningProvider())));
        var client = factory.CreateClient();
        var token = await RealReadyUserAsync(client, factory);
        var reference = (await StartKycAsync(client, token)).GetProperty("providerReference").GetString()!;
        var body = WebhookBody("evt-screen-fail-1", reference, "approved");

        using var failed = await client.SendAsync(SignedWebhook(body), TestContext.Current.CancellationToken);
        failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var rows = await ScalarAsync<long>(
            "SELECT count(*) FROM compliance.kyc_webhook_events WHERE event_id = 'evt-screen-fail-1'");
        rows.ShouldBe(0);
        var status = await ScalarAsync<string>(
            "SELECT kyc_status FROM compliance.compliance_profiles WHERE provider_reference = @r",
            ("r", reference));
        status.ShouldBe("InReview");
    }

    [Fact]
    public async Task Webhook_Concurrency_Processes_Identical_Event_Once()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client, _factory);
        var reference = (await StartKycAsync(client, token)).GetProperty("providerReference").GetString()!;
        var body = WebhookBody("evt-race-1", reference, "approved");

        var tasks = Enumerable.Range(0, 20)
            .Select(async _ =>
            {
                using var request = SignedWebhook(body);
                return await client.SendAsync(request, TestContext.Current.CancellationToken);
            })
            .ToList();
        var responses = await Task.WhenAll(tasks);
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        var webhookRows = await ScalarAsync<long>(
            "SELECT count(*) FROM compliance.kyc_webhook_events WHERE event_id = 'evt-race-1'");
        webhookRows.ShouldBe(1);
        var approvedEvents = await ScalarAsync<long>(
            """
            SELECT count(*) FROM compliance.compliance_events e
            JOIN compliance.compliance_profiles p ON p.user_id = e.user_id
            WHERE p.provider_reference = @r AND e.event_type = 'KycApproved'
            """,
            ("r", reference));
        approvedEvents.ShouldBe(1);
    }

    [Fact]
    public async Task Expired_Approval_Reads_As_Kyc_Expired_And_Allows_New_Attempt()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client, _factory);
        var reference = (await StartKycAsync(client, token)).GetProperty("providerReference").GetString()!;
        var body = WebhookBody("evt-expire-1", reference, "approved");
        using var webhook = await client.SendAsync(SignedWebhook(body), TestContext.Current.CancellationToken);
        webhook.StatusCode.ShouldBe(HttpStatusCode.OK);

        await ScalarAsync<object>(
            "UPDATE compliance.compliance_profiles SET expires_at = now() - interval '1 day' WHERE provider_reference = @r",
            ("r", reference));

        var eligibility = await EligibilityAsync(client, token);
        var realBlockers = eligibility.GetProperty("blockers").GetProperty("real")
            .EnumerateArray().Select(b => b.GetProperty("code").GetString()).ToList();
        realBlockers.ShouldContain("kyc_expired");

        var restart = await StartKycAsync(client, token);
        restart.GetProperty("kycStatus").GetString().ShouldBe("InReview");
    }

    [Fact]
    public async Task Webhook_And_Events_Store_No_Personal_Data()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client, _factory);
        var reference = (await StartKycAsync(client, token)).GetProperty("providerReference").GetString()!;
        var body = WebhookBody("evt-pii-1", reference, "approved", fullName: "Zaira Qwxy", dateOfBirth: "1988-11-30");
        using var webhook = await client.SendAsync(SignedWebhook(body), TestContext.Current.CancellationToken);
        webhook.StatusCode.ShouldBe(HttpStatusCode.OK);

        var leaked = await ScalarAsync<long>(
            """
            SELECT count(*) FROM compliance.compliance_events e
            WHERE e.metadata IS NOT NULL AND (e.metadata::text ILIKE '%Zaira%' OR e.metadata::text ILIKE '%1988%')
            """);
        leaked.ShouldBe(0);

        var columns = await ScalarAsync<long>(
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'compliance' AND table_name IN ('kyc_webhook_events', 'compliance_events')
              AND column_name IN ('full_name', 'date_of_birth', 'payload', 'body')
            """);
        columns.ShouldBe(0);
    }

    private sealed class FailingKycProvider : IKycProvider
    {
        public string Name => "fake";

        public Task<Result<KycSession>> StartVerificationAsync(
            Guid userId, string country, CancellationToken cancellationToken)
            => Task.FromResult(Result.Failure<KycSession>(new Error(
                ErrorCodes.ServiceUnavailable, "down", ErrorType.Unavailable)));

        public Task<Result<KycSession>> ResumeSessionAsync(string providerReference, CancellationToken cancellationToken)
            => Task.FromResult(Result.Failure<KycSession>(new Error(
                ErrorCodes.ServiceUnavailable, "down", ErrorType.Unavailable)));

        public KycWebhookParseResult ParseWebhook(
            string rawBody, IReadOnlyDictionary<string, string?> headers, DateTimeOffset now)
            => new(KycWebhookParseStatus.Malformed, null);
    }

    private sealed class FailingScreeningProvider : IScreeningProvider
    {
        public Task<Result<ScreeningDecision>> ScreenAsync(
            string fullName, DateOnly dateOfBirth, string? country, CancellationToken cancellationToken)
            => Task.FromResult(Result.Failure<ScreeningDecision>(new Error(
                ErrorCodes.ServiceUnavailable, "down", ErrorType.Unavailable)));
    }
}

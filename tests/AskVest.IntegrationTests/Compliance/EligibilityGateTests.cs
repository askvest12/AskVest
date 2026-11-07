using System.Net;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Compliance;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class EligibilityGateTests(PostgresFixture fixture) : IAsyncLifetime
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

    [Fact]
    public async Task Probes_Require_Authentication()
    {
        var client = Client();
        using var practice = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/diagnostics/ping"),
            TestContext.Current.CancellationToken);
        practice.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var real = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/diagnostics/ping"),
            TestContext.Current.CancellationToken);
        real.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task New_User_Gets_403_With_Exact_Blocker_Details()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);

        using var practice = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/diagnostics/ping").Auth(token),
            TestContext.Current.CancellationToken);
        practice.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var practiceEnvelope = await ComplianceTestUsers.EnvelopeAsync(practice);
        ComplianceTestUsers.ErrorCode(practiceEnvelope).ShouldBe("compliance.not_eligible");
        var practiceCodes = ComplianceTestUsers.ErrorDetailFields(practiceEnvelope);
        practiceCodes.ShouldBe(["country_required", "consent_required"], ignoreOrder: true);

        using var real = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/diagnostics/ping").Auth(token),
            TestContext.Current.CancellationToken);
        real.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var realCodes = ComplianceTestUsers.ErrorDetailFields(await ComplianceTestUsers.EnvelopeAsync(real));
        realCodes.ShouldContain("country_required");
        realCodes.ShouldContain("consent_required");
        realCodes.ShouldContain("kyc_required");
    }

    [Fact]
    public async Task Practice_Eligible_User_Reaches_Practice_But_Not_Real()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);

        using var accept = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/consents", new
            {
                documents = new[]
                {
                    new { type = "terms", version = "v1", sha256 = new string('a', 64) },
                    new { type = "privacy", version = "v1", sha256 = new string('b', 64) },
                },
            }).Auth(token),
            TestContext.Current.CancellationToken);
        accept.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var country = await client.SendAsync(
            ComplianceTestUsers.Put("/api/v1/compliance/country", new { country = "NG" }).Auth(token),
            TestContext.Current.CancellationToken);
        country.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var practice = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/diagnostics/ping").Auth(token),
            TestContext.Current.CancellationToken);
        practice.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var real = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/diagnostics/ping").Auth(token),
            TestContext.Current.CancellationToken);
        real.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var realCodes = ComplianceTestUsers.ErrorDetailFields(await ComplianceTestUsers.EnvelopeAsync(real));
        realCodes.ShouldContain("consent_required");
        realCodes.ShouldContain("kyc_required");
    }

    [Fact]
    public async Task Kill_Switch_Off_Blocks_Real_With_Real_Money_Disabled()
    {
        await using var factory = ComplianceTestUsers.CreateFactory(
            fixture,
            ComplianceTestUsers.Settings(realMoneyEnabled: false));
        var client = factory.CreateClient();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(factory);

        using var real = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/diagnostics/ping").Auth(token),
            TestContext.Current.CancellationToken);
        real.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var codes = ComplianceTestUsers.ErrorDetailFields(await ComplianceTestUsers.EnvelopeAsync(real));
        codes.ShouldContain("real_money_disabled");
    }

    [Fact]
    public async Task Probes_Are_Absent_When_The_Flag_Is_Off()
    {
        await using var factory = ComplianceTestUsers.CreateFactory(
            fixture,
            ComplianceTestUsers.Settings(probesEnabled: false));
        var client = factory.CreateClient();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(factory);

        using var practice = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/diagnostics/ping").Auth(token),
            TestContext.Current.CancellationToken);
        practice.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var real = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/diagnostics/ping").Auth(token),
            TestContext.Current.CancellationToken);
        real.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

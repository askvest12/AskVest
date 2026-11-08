using System.Net;
using System.Text.Json;
using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.MarketData.Providers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.MarketData;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class MarketDataProbeTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        var settings = new Dictionary<string, string?>(ComplianceTestUsers.Settings())
        {
            ["MarketData:PollIntervalSeconds"] = "3600",
            ["MarketData:BatchSize"] = "10",
        };
        _factory = ComplianceTestUsers.CreateFactory(fixture, settings);
        _factory.CreateClient();
        var suffix = CatalogImportTestData.Suffix();
        var result = await CatalogImportTestData.ImportFolderAsync(
            fixture,
            [],
            [],
            [CatalogImportTestData.Instrument($"instr-probe-{suffix}", null) with { Symbol = $"PRB{suffix[..4]}", ListingExchange = "NMS" }],
            TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Probe_Sets_Scenario_And_Runs_A_Poll_Cycle()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var client = _factory.CreateClient();
        var fake = _factory.Services.GetRequiredService<FakeMarketDataProvider>();
        fake.Scenario.ShouldBe("normal");

        using var response = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/diagnostics/marketdata-scenario", new { scenario = "halted" })
                .Auth(token),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await ComplianceTestUsers.DataAsync(response);
        data.GetProperty("applied").GetBoolean().ShouldBeTrue();
        fake.Scenario.ShouldBe("halted");
    }

    [Fact]
    public async Task Probe_Rejects_Unknown_Scenario()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var client = _factory.CreateClient();

        using var response = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/diagnostics/marketdata-scenario", new { scenario = "bogus" })
                .Auth(token),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var envelope = await ComplianceTestUsers.EnvelopeAsync(response);
        ComplianceTestUsers.ErrorCode(envelope).ShouldBe("validation.failed");
    }

    [Fact]
    public async Task Probe_Requires_Authentication()
    {
        var client = _factory.CreateClient();
        using var response = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/diagnostics/marketdata-scenario", new { scenario = "normal" }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Probe_Is_Not_Mapped_When_Probes_Are_Disabled()
    {
        await using var disabled = ComplianceTestUsers.CreateFactory(
            fixture,
            new Dictionary<string, string?>(ComplianceTestUsers.Settings(probesEnabled: false)));
        disabled.CreateClient();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(disabled);
        var client = disabled.CreateClient();

        using var response = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/diagnostics/marketdata-scenario", new { scenario = "normal" })
                .Auth(token),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

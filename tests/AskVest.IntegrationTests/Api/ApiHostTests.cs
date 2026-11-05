using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AskVest.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Api;

public sealed class ApiHostFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Platform", "Host=localhost;Port=1;Database=none;Username=none;Password=none");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = "Host=localhost;Port=1;Database=none;Username=none;Password=none",
                ["RateLimiting:Global:PermitLimit"] = "1000",
                ["RateLimiting:Strict:PermitLimit"] = "100",
                ["Docs:Enabled"] = "true",
            });
        });
    }
}

[Collection(ApiCollectionDefinition.Name)]
public sealed class ApiHostTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    [Fact]
    public async Task System_Info_Returns_Envelope()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/v1/system/info", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
        json.GetProperty("data").GetProperty("service").GetString().ShouldBe("askvest-api");
        json.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Health_Live_Returns_Envelope()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Correlation_Id_Is_Echoed_And_Reflected()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/system/info", UriKind.Relative));
        request.Headers.Add("X-Correlation-Id", "test-correlation-123");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.Headers.GetValues("X-Correlation-Id").ShouldContain("test-correlation-123");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("correlationId").GetString().ShouldBe("test-correlation-123");
    }

    [Fact]
    public async Task Unknown_Route_Returns_Envelope_404()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/v1/nope", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("success").GetBoolean().ShouldBeFalse();
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("route.not_found");
    }

    [Fact]
    public async Task Wrong_Method_Returns_Envelope_405()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync(new Uri("/api/v1/system/info", UriKind.Relative), null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("route.method_not_allowed");
    }

    [Fact]
    public async Task OpenApi_And_Scalar_Are_Served_When_Enabled()
    {
        var client = factory.CreateClient();
        var openApi = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        openApi.StatusCode.ShouldBe(HttpStatusCode.OK);
        var scalar = await client.GetAsync(new Uri("/scalar/v1", UriKind.Relative), TestContext.Current.CancellationToken);
        scalar.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Security_Headers_Are_Present()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/v1/system/info", UriKind.Relative), TestContext.Current.CancellationToken);
        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Content.Headers.ContentType!.ToString().ShouldContain("application/json");
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace AskVest.SmokeTests;

public sealed class ApiSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");

    private static HttpClient Client()
    {
        if (BaseUrl is null)
        {
            Assert.Skip("ASKVEST_SMOKE_BASE_URL is not set; smoke tests require a running stack.");
        }

        return new HttpClient { BaseAddress = new Uri(BaseUrl) };
    }

    [Fact]
    public async Task Health_Live_Returns_Envelope()
    {
        using var client = Client();
        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
        json.GetProperty("data").GetProperty("status").GetString().ShouldBe("Healthy");
        json.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task System_Info_Returns_Version_And_Time()
    {
        using var client = Client();
        var response = await client.GetAsync(new Uri("/api/v1/system/info", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("data").GetProperty("service").GetString().ShouldBe("askvest-api");
        json.GetProperty("data").GetProperty("serverTimeUtc").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Correlation_Id_Is_Echoed()
    {
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/system/info", UriKind.Relative));
        request.Headers.Add("X-Correlation-Id", "smoke-correlation-42");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.Headers.GetValues("X-Correlation-Id").ShouldContain("smoke-correlation-42");
    }

    [Fact]
    public async Task Unknown_Route_Returns_Envelope_404()
    {
        using var client = Client();
        var response = await client.GetAsync(new Uri("/api/v1/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("success").GetBoolean().ShouldBeFalse();
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("route.not_found");
    }

    [Fact]
    public async Task Probe_Requires_Idempotency_Key()
    {
        using var client = Client();
        using var content = new StringContent("{\"value\":\"smoke\"}", System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync(new Uri("/diagnostics/probe", UriKind.Relative), content, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("idempotency.key_missing");
    }

    [Fact]
    public async Task Probe_Replays_Identical_Key()
    {
        using var client = Client();
        var key = Guid.CreateVersion7().ToString("N");
        var first = await SendProbe(client, key, "smoke-a", TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await SendProbe(client, key, "smoke-a", TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.GetValues("Idempotent-Replay").ShouldContain("true");
        var body1 = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body2 = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body2.ShouldBe(body1);
    }

    [Fact]
    public async Task Probe_Same_Key_Different_Body_Returns_422()
    {
        using var client = Client();
        var key = Guid.CreateVersion7().ToString("N");
        await SendProbe(client, key, "smoke-x", TestContext.Current.CancellationToken);
        var second = await SendProbe(client, key, "smoke-y", TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Security_Headers_Present()
    {
        using var client = Client();
        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
    }

    private static async Task<HttpResponseMessage> SendProbe(HttpClient client, string key, string value, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/diagnostics/probe", UriKind.Relative))
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { value }), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, cancellationToken);
    }
}

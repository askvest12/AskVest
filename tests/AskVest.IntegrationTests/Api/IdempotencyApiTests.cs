using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AskVest.Api;
using AskVest.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Api;

public sealed class IdempotencyApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Platform", connectionString);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = connectionString,
                ["Diagnostics:EnableProbes"] = "true",
                ["RateLimiting:Global:PermitLimit"] = "10000",
                ["Docs:Enabled"] = "false",
            });
            foreach (var (key, value) in AskVest.IntegrationTests.Infrastructure.TestIdentitySecrets.Entries)
            {
                config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
            }
        });
    }
}

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdempotencyApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private IdempotencyApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new IdempotencyApiFactory(fixture.AppConnectionString);
        _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    private static StringContent Body(string value) => new(JsonSerializer.Serialize(new { value }), System.Text.Encoding.UTF8, "application/json");

    private static HttpRequestMessage Probe(string key, string value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/diagnostics/probe", UriKind.Relative))
        {
            Content = Body(value),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [Fact]
    public async Task Missing_Key_Returns_400()
    {
        var client = _factory.CreateClient();
        using var content = Body("x");
        var response = await client.PostAsync(new Uri("/diagnostics/probe", UriKind.Relative), content, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("idempotency.key_missing");
    }

    [Fact]
    public async Task Invalid_Key_Returns_400()
    {
        var client = _factory.CreateClient();
        using var request = Probe("bad key!", "x");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("idempotency.key_invalid");
    }

    [Fact]
    public async Task Replay_Returns_Stored_Response()
    {
        var client = _factory.CreateClient();
        var key = Guid.CreateVersion7().ToString("N");
        using var firstRequest = Probe(key, "alpha");
        var first = await client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var secondRequest = Probe(key, "alpha");
        var second = await client.SendAsync(secondRequest, TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.GetValues("Idempotent-Replay").ShouldContain("true");
        var body1 = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body2 = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body2.ShouldBe(body1);
    }

    [Fact]
    public async Task Same_Key_Different_Body_Returns_422()
    {
        var client = _factory.CreateClient();
        var key = Guid.CreateVersion7().ToString("N");
        using var firstRequest = Probe(key, "alpha");
        await client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        using var secondRequest = Probe(key, "beta");
        var second = await client.SendAsync(secondRequest, TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var json = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("idempotency.key_reused");
    }

    [Fact]
    public async Task Concurrent_Same_Key_One_Wins()
    {
        var client = _factory.CreateClient();
        var key = Guid.CreateVersion7().ToString("N");
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(async _ =>
            {
                using var request = Probe(key, "race");
                return await client.SendAsync(request, TestContext.Current.CancellationToken);
            }));
        var codes = responses.Select(r => (int)r.StatusCode).OrderBy(c => c).ToList();
        codes.ShouldContain((int)HttpStatusCode.OK);
        codes.ShouldContain((int)HttpStatusCode.Conflict);
        foreach (var response in responses)
        {
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
        }
    }
}

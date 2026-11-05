using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AskVest.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Identity;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class InputHardeningApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new AuthApiFactory(fixture.AppConnectionString);
        _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private static HttpRequestMessage Post(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", $"k-{Guid.CreateVersion7():N}");
        return request;
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError, "hostile input must never produce a 500");
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("' OR '1'='1")]
    [InlineData("admin@example.com'--")]
    [InlineData("'; DROP TABLE identity.users; --")]
    public async Task SignUp_With_Sql_Injection_Style_Email_Is_Rejected_Or_Uniform(string email)
    {
        var json = await SendAsync(Client(), Post("/api/v1/auth/sign-up", new
        {
            email,
            password = "CorrectHorse9",
        }));

        json.GetProperty("success").GetBoolean().ShouldBeFalse();
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("validation.failed");
    }

    [Fact]
    public async Task Login_With_Sql_Injection_Style_Email_Is_Uniform()
    {
        var json = await SendAsync(Client(), Post("/api/v1/auth/login", new
        {
            email = "' OR '1'='1",
            password = "WrongPassword1",
            device = new { installationId = "sqli", platform = "android", deviceName = "x" },
        }));

        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
    }

    [Fact]
    public async Task Oversized_Fields_Are_Rejected_With_Validation_Errors()
    {
        var client = Client();

        var longEmail = await SendAsync(client, Post("/api/v1/auth/sign-up", new
        {
            email = $"{new string('a', 300)}@example.com",
            password = "CorrectHorse9",
        }));
        longEmail.GetProperty("error").GetProperty("code").GetString().ShouldBe("validation.failed");

        var longPassword = await SendAsync(client, Post("/api/v1/auth/sign-up", new
        {
            email = $"long-{Guid.CreateVersion7():N}@example.com",
            password = new string('x', 500),
        }));
        longPassword.GetProperty("error").GetProperty("code").GetString().ShouldBe("validation.failed");

        var longDevice = await SendAsync(client, Post("/api/v1/auth/login", new
        {
            email = $"sqli2-{Guid.CreateVersion7():N}@example.com",
            password = "WrongPassword1",
            device = new
            {
                installationId = "x",
                platform = "android",
                deviceName = new string('n', 5000),
            },
        }));
        longDevice.GetProperty("error").GetProperty("code").GetString().ShouldNotBeNull();
        longDevice.GetProperty("success").GetBoolean().ShouldBeFalse();
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Identity.Messaging;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Identity;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class NoPlaintextSecretsApiTests(PostgresFixture fixture) : IAsyncLifetime
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

    private const string Password = "N0Plaintext!Sweep#77";
    private const string Pin = "135790";

    private static HttpRequestMessage Post(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", $"k-{Guid.CreateVersion7():N}");
        return request;
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, object body)
    {
        using var response = await client.SendAsync(Post(path, body), TestContext.Current.CancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private async Task<EmailMessage> WaitForEmailAsync(string to)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var match = _factory.Emails.Sent.LastOrDefault(m => m.To == to);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No email captured for {to}.");
    }

    private static string CodeFrom(EmailMessage email)
        => Regex.Match(email.TextBody, @"code is (\d{6})").Groups[1].Value;

    [Fact]
    public async Task Database_Stores_No_Plaintext_Secrets()
    {
        var client = Client();
        var email = $"sweep-{Guid.CreateVersion7():N}@example.com";
        var sweepSecrets = new List<string> { Password };
        var shortSecrets = new List<(string Table, string Column, string Value)>();

        await PostJsonAsync(client, "/api/v1/auth/sign-up", new { email, password = Password });
        var verifyCode = CodeFrom(await WaitForEmailAsync(email));
        shortSecrets.Add(("one_time_codes", "code_hash", verifyCode));

        await PostJsonAsync(client, "/api/v1/auth/verify-email", new { email, code = verifyCode });

        var login = await PostJsonAsync(client, "/api/v1/auth/login", new
        {
            email,
            password = Password,
            device = new { installationId = "sweep-dev-a", platform = "android", deviceName = "Sweep A" },
        });
        var accessToken = login.GetProperty("data").GetProperty("accessToken").GetString()!;
        var refreshToken = login.GetProperty("data").GetProperty("refreshToken").GetString()!;
        sweepSecrets.Add(accessToken);
        sweepSecrets.Add(refreshToken);

        var loginB = await PostJsonAsync(client, "/api/v1/auth/login", new
        {
            email,
            password = Password,
            device = new { installationId = "sweep-dev-b", platform = "ios", deviceName = "Sweep B" },
        });
        loginB.GetProperty("data").GetProperty("result").GetString().ShouldBe("device_verification_required");
        var deviceCode = CodeFrom(await WaitForEmailAsync(email));
        shortSecrets.Add(("one_time_codes", "code_hash", deviceCode));

        var verifyDevice = await PostJsonAsync(client, "/api/v1/auth/verify-device", new
        {
            challengeId = loginB.GetProperty("data").GetProperty("challengeId").GetString(),
            code = deviceCode,
        });
        sweepSecrets.Add(verifyDevice.GetProperty("data").GetProperty("accessToken").GetString()!);
        sweepSecrets.Add(verifyDevice.GetProperty("data").GetProperty("refreshToken").GetString()!);

        using var authed = Client();
        authed.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        await PostJsonAsync(authed, "/api/v1/account/pin", new { password = Password, pin = Pin });
        shortSecrets.Add(("users", "pin_hash", Pin));

        var stepUp = await PostJsonAsync(authed, "/api/v1/auth/step-up", new
        {
            purpose = "delete_account",
            pin = Pin,
        });
        sweepSecrets.Add(stepUp.GetProperty("data").GetProperty("grantToken").GetString()!);

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var columns = new List<(string Table, string Column)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = 'identity'
              AND data_type IN ('text', 'character varying', 'character', 'jsonb', 'json')
            ORDER BY table_name, column_name
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        columns.ShouldNotBeEmpty();

        foreach (var (table, column) in columns)
        {
            foreach (var secret in sweepSecrets)
            {
                var count = await ScalarLongAsync(
                    connection,
                    $"SELECT count(*) FROM identity.\"{table}\" WHERE position(@secret in \"{column}\"::text) > 0",
                    secret);
                count.ShouldBe(0, $"Plaintext secret found in identity.{table}.{column}");
            }
        }

        foreach (var (table, column, value) in shortSecrets)
        {
            var count = await ScalarLongAsync(
                connection,
                $"SELECT count(*) FROM identity.\"{table}\" WHERE \"{column}\" = @value",
                value);
            count.ShouldBe(0, $"Plaintext secret found in identity.{table}.{column}");
        }
    }

    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection connection, string sql, string value)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("secret", value);
        command.Parameters.AddWithValue("value", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace AskVest.SmokeTests;

[Collection("SmokeStack")]
public sealed class CatalogSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");
    private static readonly string? MailpitUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_MAILPIT_URL");

    private const string Password = "CorrectHorseSmoke9";

    private static HttpClient Client()
    {
        if (BaseUrl is null)
        {
            Assert.Skip("ASKVEST_SMOKE_BASE_URL is not set; smoke tests require a running stack.");
        }

        return new HttpClient { BaseAddress = new Uri(BaseUrl) };
    }

    private static HttpClient Mailpit()
    {
        if (MailpitUrl is null)
        {
            Assert.Skip("ASKVEST_SMOKE_MAILPIT_URL is not set; smoke tests require Mailpit.");
        }

        return new HttpClient { BaseAddress = new Uri(MailpitUrl) };
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body, string? token)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (method == HttpMethod.Post || method == HttpMethod.Put)
        {
            request.Headers.Add("Idempotency-Key", $"k-{Guid.CreateVersion7():N}");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<JsonElement> SendJsonAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            await Task.Delay(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);
            using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
            if (request.Content is not null)
            {
                retry.Content = new StringContent(
                    await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                    System.Text.Encoding.UTF8,
                    "application/json");
            }

            foreach (var header in request.Headers)
            {
                retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var retried = await client.SendAsync(retry, TestContext.Current.CancellationToken);
            return await retried.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> SendJsonWithStatusAsync(
        HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(expected);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static JsonElement RequireData(JsonElement json)
    {
        if (!json.TryGetProperty("data", out var data))
        {
            throw new InvalidOperationException($"Expected data, got: {json.GetRawText()}");
        }

        return data;
    }

    private static async Task<string> WaitForCodeAsync(HttpClient mailpit, string to, string subjectContains)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(60);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var matches = await mailpit.GetFromJsonAsync<JsonElement>(
                new Uri("/api/v1/messages?limit=50", UriKind.Relative), TestContext.Current.CancellationToken);
            var messages = matches.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("To").EnumerateArray().Any(t => t.GetProperty("Address").GetString() == to))
                .Where(m => m.GetProperty("Subject").GetString()!.Contains(subjectContains, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (messages.Count > 0)
            {
                var latest = messages[^1];
                var detail = await mailpit.GetFromJsonAsync<JsonElement>(
                    new Uri($"/api/v1/message/{latest.GetProperty("ID").GetString()}", UriKind.Relative),
                    TestContext.Current.CancellationToken);
                var text = detail.GetProperty("Text").GetString() ?? string.Empty;
                var match = System.Text.RegularExpressions.Regex.Match(text, @"code is (\d{6})");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No '{subjectContains}' email captured for {to}.");
    }

    private static async Task<string> SignupVerifiedAsync(HttpClient client, HttpClient mailpit, string email)
    {
        var signUp = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/sign-up",
            new { email, password = Password }, null));
        signUp.GetProperty("success").GetBoolean().ShouldBeTrue();
        var verifyCode = await WaitForCodeAsync(mailpit, email, "Verify");
        var verify = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/verify-email",
            new { email, code = verifyCode }, null));
        verify.GetProperty("success").GetBoolean().ShouldBeTrue();
        var login = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/login", new
        {
            email,
            password = Password,
            device = new { installationId = $"dev-{Guid.CreateVersion7():N}", platform = "android", deviceName = "Smoke", appVersion = "1.0.0" },
        }, null));
        return RequireData(login).GetProperty("accessToken").GetString()!;
    }

    private static List<JsonElement> TokensOf(JsonElement instrumentDetail)
        => RequireData(instrumentDetail).GetProperty("tokens").EnumerateArray().ToList();

    private static JsonElement TokenBySymbol(JsonElement instrumentDetail, string symbol)
        => TokensOf(instrumentDetail).Single(t => t.GetProperty("symbol").GetString() == symbol);

    private static void Availability(JsonElement token, bool buyable, params string[] buyBlockers)
    {
        var availability = token.GetProperty("availability");
        availability.GetProperty("buyable").GetBoolean().ShouldBe(buyable);
        var blockers = availability.GetProperty("buyBlockers").EnumerateArray()
            .Select(b => b.GetString()!).OrderBy(b => b, StringComparer.Ordinal).ToList();
        blockers.ShouldBe(buyBlockers.OrderBy(b => b, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task Catalog_Journey_Browse_Detail_Availability_And_Isolation()
    {
        using var client = Client();
        using var mailpit = Mailpit();

        // Unauthenticated requests are refused.
        var anonymous = await SendJsonWithStatusAsync(
            client, Request(HttpMethod.Get, "/api/v1/catalog/instruments", null, null),
            HttpStatusCode.Unauthorized);
        anonymous.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.unauthenticated");

        var email = $"smoke-c-{Guid.CreateVersion7():N}@example.com";
        var token = await SignupVerifiedAsync(client, mailpit, email);
        var country = await SendJsonAsync(client, Request(HttpMethod.Put, "/api/v1/compliance/country",
            new { country = "NG" }, token));
        country.GetProperty("success").GetBoolean().ShouldBeTrue();

        // Browse: four active sample instruments, kind filter and search.
        var all = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/catalog/instruments", null, token));
        var items = RequireData(all).GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(4);
        items.Select(i => i.GetProperty("symbol").GetString())
            .ShouldBe(["SAPL", "SGETF", "SNVDA", "SHFX"], ignoreOrder: true);

        var stocks = await SendJsonAsync(client,
            Request(HttpMethod.Get, "/api/v1/catalog/instruments?kind=stock", null, token));
        RequireData(stocks).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("kind").GetString()).ShouldAllBe(k => k == "stock");

        var searched = await SendJsonAsync(client,
            Request(HttpMethod.Get, "/api/v1/catalog/instruments?search=apple", null, token));
        RequireData(searched).GetProperty("items").EnumerateArray().Count().ShouldBe(1);

        // Pagination: page size 2 walks all four instruments exactly once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var path = cursor is null
                ? "/api/v1/catalog/instruments?limit=2"
                : $"/api/v1/catalog/instruments?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var page = await SendJsonAsync(client, Request(HttpMethod.Get, path, null, token));
            var data = RequireData(page);
            foreach (var item in data.GetProperty("items").EnumerateArray())
            {
                seen.Add(item.GetProperty("id").GetString()!);
            }

            cursor = data.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }
        while (cursor is not null);

        seen.Count.ShouldBe(4);

        // Instrument detail: every token carries availability for the NG user.
        var nvdaDetail = await SendJsonAsync(client,
            Request(HttpMethod.Get, "/api/v1/catalog/instruments/sample-nvda", null, token));
        var nvdaTokens = TokensOf(nvdaDetail);
        nvdaTokens.Count.ShouldBe(3);

        Availability(TokenBySymbol(nvdaDetail, "SNVDA"), buyable: true);
        var suspended = TokenBySymbol(nvdaDetail, "SNVDA-S");
        Availability(suspended, buyable: false, "token_not_listed");
        suspended.GetProperty("availability").GetProperty("sellBlockers").EnumerateArray()
            .Select(b => b.GetString()).ShouldBe(["token_suspended"]);
        var delisted = TokenBySymbol(nvdaDetail, "SNVDA-D");
        Availability(delisted, buyable: false, "token_not_listed");
        delisted.GetProperty("availability").GetProperty("sellable").GetBoolean().ShouldBeTrue();

        var aplDetail = await SendJsonAsync(client,
            Request(HttpMethod.Get, "/api/v1/catalog/instruments/sample-aapl", null, token));
        Availability(TokenBySymbol(aplDetail, "SAPL"), buyable: false, "token_not_listed");
        Availability(TokenBySymbol(aplDetail, "SAPL-E"), buyable: false, "disclosure_review_expired");
        Availability(TokenBySymbol(aplDetail, "SAPL-H"), buyable: false, "chain_disabled");

        var etfDetail = await SendJsonAsync(client,
            Request(HttpMethod.Get, "/api/v1/catalog/instruments/sample-global-etf", null, token));
        Availability(TokenBySymbol(etfDetail, "SGETF"), buyable: false, "country_not_allowed");
        Availability(TokenBySymbol(etfDetail, "SGETF-X"), buyable: false, "issuer_not_approved");

        // Token detail: full disclosure fields, no gateway key or reviewer label.
        var healthy = TokenBySymbol(nvdaDetail, "SNVDA");
        var tokenDetail = await SendJsonAsync(client,
            Request(HttpMethod.Get, $"/api/v1/catalog/tokens/{healthy.GetProperty("id").GetGuid()}", null, token));
        var detail = RequireData(tokenDetail);
        detail.GetProperty("claim").GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
        detail.GetProperty("backing").GetString().ShouldNotBeNullOrWhiteSpace();
        detail.GetProperty("custodian").GetString().ShouldNotBeNullOrWhiteSpace();
        detail.GetProperty("redemptionAndExit").GetString().ShouldNotBeNullOrWhiteSpace();
        detail.GetProperty("dividendPolicy").GetProperty("policy").GetString().ShouldNotBeNullOrWhiteSpace();
        detail.GetProperty("votingRights").GetProperty("allowed").GetBoolean().ShouldBeTrue();
        detail.GetProperty("transferRestrictions").GetString().ShouldNotBeNullOrWhiteSpace();
        detail.GetProperty("riskStatements").GetArrayLength().ShouldBe(3);
        detail.GetProperty("disclosureDocumentLink").GetString().ShouldNotBeNullOrWhiteSpace();
        detail.GetProperty("tradingSchedule").GetProperty("kind").GetString().ShouldBe("always");
        detail.GetProperty("allowedCountries").EnumerateArray().Select(c => c.GetString())
            .ShouldBe(["GB", "KE", "NG", "US"], ignoreOrder: true);
        detail.GetProperty("explorerLink").GetString()!.ShouldContain("sepolia.etherscan.io");
        var raw = tokenDetail.GetRawText();
        raw.ShouldNotContain("sample-gateway");
        raw.ShouldNotContain("sample-reviewer");
        raw.ShouldNotContain("gatewayKey");
        raw.ShouldNotContain("reviewerLabel");

        // Chains: only the enabled Sepolia sample appears.
        var chains = await SendJsonAsync(client,
            Request(HttpMethod.Get, "/api/v1/catalog/chains", null, token));
        var chainItems = RequireData(chains).EnumerateArray().ToList();
        chainItems.Count.ShouldBe(1);
        chainItems[0].GetProperty("id").GetString().ShouldBe("eip155:11155111");

        // Unknown resources answer not found.
        var missingInstrument = await SendJsonWithStatusAsync(client,
            Request(HttpMethod.Get, "/api/v1/catalog/instruments/nope", null, token),
            HttpStatusCode.NotFound);
        missingInstrument.GetProperty("error").GetProperty("code").GetString().ShouldBe("resource.not_found");
        var missingToken = await SendJsonWithStatusAsync(client,
            Request(HttpMethod.Get, $"/api/v1/catalog/tokens/{Guid.CreateVersion7()}", null, token),
            HttpStatusCode.NotFound);
        missingToken.GetProperty("error").GetProperty("code").GetString().ShouldBe("resource.not_found");

        // A user with no country gets country_unknown on every listed token.
        var emailB = $"smoke-c2-{Guid.CreateVersion7():N}@example.com";
        var tokenB = await SignupVerifiedAsync(client, mailpit, emailB);
        var nvdaForB = await SendJsonAsync(client,
            Request(HttpMethod.Get, "/api/v1/catalog/instruments/sample-nvda", null, tokenB));
        Availability(TokenBySymbol(nvdaForB, "SNVDA"), buyable: false, "country_unknown");
    }
}

using System.Net;
using System.Text.Json;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Catalog.Import.Data;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Catalog;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class CatalogApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = ComplianceTestUsers.CreateFactory(fixture);
        _factory.CreateClient();
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.DataAsync(response);

    private static async Task<JsonElement> EnvelopeAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.EnvelopeAsync(response);

    private async Task<string> VerifiedUserAsync()
        => await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);

    private async Task ImportAsync(
        IReadOnlyList<ChainData> chains,
        IReadOnlyList<IssuerData> issuers,
        IReadOnlyList<InstrumentData> instruments)
    {
        var result = await CatalogImportTestData.ImportFolderAsync(
            fixture, chains, issuers, instruments, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    private static ChainData Chain(string id, string status = "enabled")
        => CatalogImportTestData.Chain(id, status);

    private static IssuerData Issuer(string id, string status = "approved")
        => CatalogImportTestData.Issuer(id, status);

    private static InstrumentData Instrument(string id, string symbol, string name, string kind, params TokenData[] tokens)
        => CatalogImportTestData.Instrument(id, tokens) with
        {
            Symbol = symbol,
            Name = name,
            Kind = kind,
        };

    private static Guid UserIdFromToken(string token)
    {
        var payload = token.Split('.')[1];
        var json = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(payload)).RootElement;
        return Guid.Parse(json.GetProperty("sub").GetString()!);
    }

    [Fact]
    public async Task Catalog_Endpoints_Require_Authentication()
    {
        var client = Client();
        foreach (var path in new[]
        {
            "/api/v1/catalog/instruments",
            "/api/v1/catalog/instruments/some-instrument",
            "/api/v1/catalog/tokens/" + Guid.CreateVersion7(),
            "/api/v1/catalog/chains",
        })
        {
            using var response = await client.SendAsync(
                ComplianceTestUsers.Get(path), TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Instruments_List_Paginates_Filters_And_Searches()
    {
        var suffix = CatalogImportTestData.Suffix();
        var symbolPrefix = $"Z{suffix}";
        var marker = $"Name-{suffix}";
        await ImportAsync(
            [Chain("eip155:11155111")],
            [Issuer($"api-issuer-{suffix}")],
            [
                Instrument($"api-stock-a-{suffix}", $"{symbolPrefix}A", $"{marker} Alpha", "stock", CatalogImportTestData.Token(
                    issuerId: $"api-issuer-{suffix}", address: CatalogImportTestData.Address(suffix, '1'),
                    assetCode: CatalogImportTestData.AssetCode("ASA", suffix))),
                Instrument($"api-stock-b-{suffix}", $"{symbolPrefix}B", $"{marker} Beta", "stock", CatalogImportTestData.Token(
                    issuerId: $"api-issuer-{suffix}", address: CatalogImportTestData.Address(suffix, '2'),
                    assetCode: CatalogImportTestData.AssetCode("ASB", suffix))),
                Instrument($"api-etf-{suffix}", $"{symbolPrefix}E", $"{marker} Fund", "etf", CatalogImportTestData.Token(
                    issuerId: $"api-issuer-{suffix}", address: CatalogImportTestData.Address(suffix, '3'),
                    assetCode: CatalogImportTestData.AssetCode("ASE", suffix))),
            ]);

        var token = await VerifiedUserAsync();
        var client = Client();
        var search = $"/api/v1/catalog/instruments?search={symbolPrefix}&limit=2";

        using var first = await client.SendAsync(
            ComplianceTestUsers.Get(search).Auth(token), TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page1 = await DataAsync(first);
        page1.GetProperty("items").GetArrayLength().ShouldBe(2);
        page1.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        var cursor = page1.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();

        using var second = await client.SendAsync(
            ComplianceTestUsers.Get($"{search}&cursor={Uri.EscapeDataString(cursor!)}").Auth(token),
            TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page2 = await DataAsync(second);
        page2.GetProperty("items").GetArrayLength().ShouldBe(1);
        page2.GetProperty("hasMore").GetBoolean().ShouldBeFalse();

        var symbols = page1.GetProperty("items").EnumerateArray()
            .Concat(page2.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("symbol").GetString())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        symbols.ShouldBe([$"{symbolPrefix}A", $"{symbolPrefix}B", $"{symbolPrefix}E"]);

        using var stocks = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/catalog/instruments?kind=stock&search={symbolPrefix}").Auth(token),
            TestContext.Current.CancellationToken);
        var stockItems = (await DataAsync(stocks)).GetProperty("items");
        stockItems.GetArrayLength().ShouldBe(2);
        stockItems.EnumerateArray().Select(i => i.GetProperty("kind").GetString())
            .ShouldAllBe(k => k == "stock");

        using var etf = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/catalog/instruments?kind=etf&search={symbolPrefix}").Auth(token),
            TestContext.Current.CancellationToken);
        (await DataAsync(etf)).GetProperty("items").GetArrayLength().ShouldBe(1);

        using var byName = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/catalog/instruments?search={Uri.EscapeDataString(marker + " Beta")}").Auth(token),
            TestContext.Current.CancellationToken);
        var nameItems = (await DataAsync(byName)).GetProperty("items");
        nameItems.GetArrayLength().ShouldBe(1);
        nameItems[0].GetProperty("symbol").GetString().ShouldBe($"{symbolPrefix}B");

        using var badKind = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/catalog/instruments?kind=bond").Auth(token),
            TestContext.Current.CancellationToken);
        badKind.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ComplianceTestUsers.ErrorCode(await EnvelopeAsync(badKind)).ShouldBe("validation.failed");
    }

    [Fact]
    public async Task Instrument_Detail_Lists_Tokens_With_Availability()
    {
        var suffix = CatalogImportTestData.Suffix();
        var issuerId = $"api-issuer-{suffix}";
        await ImportAsync(
            [Chain("eip155:11155111")],
            [Issuer(issuerId)],
            [CatalogImportTestData.Instrument($"api-instr-{suffix}", [
                CatalogImportTestData.Token(issuerId: issuerId, address: CatalogImportTestData.Address(suffix, '1'),
                    assetCode: CatalogImportTestData.AssetCode("IDL", suffix), status: "listed"),
                CatalogImportTestData.Token(issuerId: issuerId, address: CatalogImportTestData.Address(suffix, '2'),
                    assetCode: CatalogImportTestData.AssetCode("IDS", suffix), status: "suspended", everListed: true),
                CatalogImportTestData.Token(issuerId: issuerId, address: CatalogImportTestData.Address(suffix, '3'),
                    assetCode: CatalogImportTestData.AssetCode("IDD", suffix), status: "delisted", everListed: true),
            ])]);

        var token = await VerifiedUserAsync();
        await ComplianceTestUsers.SetCountryAsync(Client(), token, "NG");
        var client = Client();

        using var response = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/catalog/instruments/api-instr-{suffix}").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await DataAsync(response);
        data.GetProperty("instrument").GetProperty("id").GetString().ShouldBe($"api-instr-{suffix}");

        var tokens = data.GetProperty("tokens").EnumerateArray().ToList();
        tokens.Count.ShouldBe(3);

        var listed = tokens.Single(t => t.GetProperty("status").GetString() == "listed");
        listed.GetProperty("availability").GetProperty("buyable").GetBoolean().ShouldBeTrue();
        listed.GetProperty("availability").GetProperty("sellable").GetBoolean().ShouldBeTrue();

        var suspended = tokens.Single(t => t.GetProperty("status").GetString() == "suspended");
        suspended.GetProperty("availability").GetProperty("buyable").GetBoolean().ShouldBeFalse();
        suspended.GetProperty("availability").GetProperty("sellable").GetBoolean().ShouldBeFalse();

        var delisted = tokens.Single(t => t.GetProperty("status").GetString() == "delisted");
        delisted.GetProperty("availability").GetProperty("buyable").GetBoolean().ShouldBeFalse();
        delisted.GetProperty("availability").GetProperty("sellable").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Token_Detail_Returns_Disclosures_Without_Gateway_Key_Or_Reviewer()
    {
        var suffix = CatalogImportTestData.Suffix();
        var issuerId = $"api-issuer-{suffix}";
        var assetCode = CatalogImportTestData.AssetCode("TDT", suffix);
        await ImportAsync(
            [Chain("eip155:11155111")],
            [Issuer(issuerId)],
            [CatalogImportTestData.Instrument($"api-instr-{suffix}", [
                CatalogImportTestData.Token(issuerId: issuerId, address: CatalogImportTestData.Address(suffix, '1'),
                    assetCode: assetCode, status: "listed"),
            ])]);

        var token = await VerifiedUserAsync();
        await ComplianceTestUsers.SetCountryAsync(Client(), token, "NG");
        var client = Client();
        var tokenId = await CatalogImportTestData.TokenIdByAssetCodeAsync(
            fixture, assetCode, TestContext.Current.CancellationToken);

        using var response = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/catalog/tokens/{tokenId}").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.ShouldNotContain("gatewayKey");
        raw.ShouldNotContain("reviewerLabel");
        raw.ShouldNotContain("sample-reviewer");

        var data = JsonDocument.Parse(raw).RootElement.GetProperty("data");
        data.GetProperty("id").GetGuid().ShouldBe(tokenId);
        data.GetProperty("status").GetString().ShouldBe("listed");
        data.GetProperty("decimals").GetInt32().ShouldBe(18);
        data.GetProperty("ledgerAssetCode").GetString().ShouldBe(assetCode);
        data.GetProperty("caip19").GetString()!.ShouldStartWith("eip155:11155111");
        data.GetProperty("contractAddress").GetString()!.ShouldStartWith("0x");
        data.GetProperty("explorerLink").GetString()!.ShouldContain("example.com");
        data.GetProperty("chain").GetProperty("id").GetString().ShouldBe("eip155:11155111");
        data.GetProperty("issuer").GetProperty("legalName").GetString()!.ShouldContain("Sample Issuer");
        data.GetProperty("claim").GetProperty("type").GetString().ShouldNotBeNullOrWhiteSpace();
        data.GetProperty("backing").GetString().ShouldNotBeNullOrWhiteSpace();
        data.GetProperty("custodian").GetString().ShouldNotBeNullOrWhiteSpace();
        data.GetProperty("redemptionAndExit").GetString().ShouldNotBeNullOrWhiteSpace();
        data.GetProperty("dividendPolicy").GetProperty("policy").GetString().ShouldNotBeNullOrWhiteSpace();
        data.GetProperty("votingRights").GetProperty("allowed").GetBoolean().ShouldBeTrue();
        data.GetProperty("transferRestrictions").GetString().ShouldNotBeNullOrWhiteSpace();
        data.GetProperty("issuerFees").GetArrayLength().ShouldBe(1);
        data.GetProperty("riskStatements").GetArrayLength().ShouldBeGreaterThanOrEqualTo(3);
        data.GetProperty("disclosureDocumentLink").GetString()!.ShouldStartWith("https://");
        data.GetProperty("tradingSchedule").GetProperty("kind").GetString().ShouldBe("always");
        data.GetProperty("reviewedAt").GetString().ShouldNotBeNullOrWhiteSpace();
        data.GetProperty("reviewExpiresAt").GetString().ShouldNotBeNullOrWhiteSpace();
        data.GetProperty("allowedCountries").EnumerateArray()
            .Select(c => c.GetString()).ShouldContain("NG");
        var availability = data.GetProperty("availability");
        availability.GetProperty("buyable").GetBoolean().ShouldBeTrue();
        availability.GetProperty("sellable").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Chains_List_Enabled_Only()
    {
        var suffix = CatalogImportTestData.Suffix();
        await ImportAsync(
            [Chain("eip155:11155111"), Chain("eip155:17000", "disabled")],
            [Issuer($"api-issuer-{suffix}")],
            [CatalogImportTestData.Instrument($"api-instr-{suffix}", [
                CatalogImportTestData.Token(
                    issuerId: $"api-issuer-{suffix}", address: CatalogImportTestData.Address(suffix, '1'),
                    assetCode: CatalogImportTestData.AssetCode("CHN", suffix)),
            ])]);

        var token = await VerifiedUserAsync();
        var client = Client();

        using var response = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/catalog/chains").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var chains = (await DataAsync(response)).EnumerateArray().ToList();
        chains.ShouldContain(c => c.GetProperty("id").GetString() == "eip155:11155111");
        chains.ShouldNotContain(c => c.GetProperty("id").GetString() == "eip155:17000");
        var sepolia = chains.Single(c => c.GetProperty("id").GetString() == "eip155:11155111");
        sepolia.GetProperty("family").GetString().ShouldBe("evm");
        sepolia.GetProperty("nativeAssetSymbol").GetString().ShouldBe("ETH");
        sepolia.GetProperty("explorerLinkTemplate").GetString()!.ShouldContain("example.com");
    }

    [Fact]
    public async Task Unknown_Ids_Return_Not_Found()
    {
        var token = await VerifiedUserAsync();
        var client = Client();

        using var instrument = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/catalog/instruments/no-such-instrument").Auth(token),
            TestContext.Current.CancellationToken);
        instrument.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ComplianceTestUsers.ErrorCode(await EnvelopeAsync(instrument)).ShouldBe("resource.not_found");

        using var catalogToken = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/catalog/tokens/{Guid.CreateVersion7()}").Auth(token),
            TestContext.Current.CancellationToken);
        catalogToken.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ComplianceTestUsers.ErrorCode(await EnvelopeAsync(catalogToken)).ShouldBe("resource.not_found");
    }

    [Fact]
    public async Task Availability_Reflects_Each_Token_Case()
    {
        var suffix = CatalogImportTestData.Suffix();
        var issuerId = $"api-issuer-{suffix}";
        var suspendedIssuerId = $"api-issuer-s-{suffix}";
        var enabledChain = "eip155:11155111";
        var disabledChain = "eip155:17000";
        var instrumentId = $"api-instr-{suffix}";
        var pastReview = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var pastExpiry = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        TokenData TokenAt(char fill, string tag, string issuer, string chain, string status, bool everListed,
            string[]? countries = null, DateTimeOffset? reviewedAt = null, DateTimeOffset? reviewExpiresAt = null)
            => CatalogImportTestData.Token(
                issuerId: issuer, chainId: chain, address: CatalogImportTestData.Address(suffix, fill),
                status: status, everListed: everListed, assetCode: $"AV{suffix.ToUpperInvariant()[..4]}{tag}",
                countries: countries, reviewedAt: reviewedAt, reviewExpiresAt: reviewExpiresAt);

        await ImportAsync(
            [Chain(enabledChain), Chain(disabledChain, "disabled")],
            [Issuer(issuerId), Issuer(suspendedIssuerId, "suspended")],
            [CatalogImportTestData.Instrument(instrumentId, [
                TokenAt('1', "H1", issuerId, enabledChain, "listed", false),
                TokenAt('2', "S1", issuerId, enabledChain, "suspended", true),
                TokenAt('3', "D1", issuerId, enabledChain, "delisted", true),
                TokenAt('4', "E1", issuerId, enabledChain, "listed", true, reviewedAt: pastReview, reviewExpiresAt: pastExpiry),
                TokenAt('5', "C1", issuerId, disabledChain, "listed", true),
                TokenAt('6', "W1", issuerId, enabledChain, "draft", false),
                TokenAt('7', "U1", issuerId, enabledChain, "listed", false, countries: ["US"]),
                TokenAt('8', "I1", suspendedIssuerId, enabledChain, "listed", true),
            ])]);

        var client = Client();

        async Task<JsonElement> AvailabilityForAsync(string tokenJwt, string assetCode)
        {
            var id = await CatalogImportTestData.TokenIdByAssetCodeAsync(
                fixture, assetCode, TestContext.Current.CancellationToken);
            using var response = await client.SendAsync(
                ComplianceTestUsers.Get($"/api/v1/catalog/tokens/{id}").Auth(tokenJwt),
                TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            return (await DataAsync(response)).GetProperty("availability").Clone();
        }

        static List<string> Blockers(JsonElement availability, string side)
            => availability.GetProperty(side).EnumerateArray().Select(b => b.GetString()!).ToList();

        var ngUser = await VerifiedUserAsync();
        await ComplianceTestUsers.SetCountryAsync(client, ngUser, "NG");

        (await AvailabilityForAsync(ngUser, $"AV{suffix.ToUpperInvariant()[..4]}H1"))
            .GetProperty("buyable").GetBoolean().ShouldBeTrue();

        var suspended = await AvailabilityForAsync(ngUser, $"AV{suffix.ToUpperInvariant()[..4]}S1");
        suspended.GetProperty("buyable").GetBoolean().ShouldBeFalse();
        suspended.GetProperty("sellable").GetBoolean().ShouldBeFalse();
        Blockers(suspended, "sellBlockers").ShouldContain("token_suspended");

        var delisted = await AvailabilityForAsync(ngUser, $"AV{suffix.ToUpperInvariant()[..4]}D1");
        delisted.GetProperty("buyable").GetBoolean().ShouldBeFalse();
        delisted.GetProperty("sellable").GetBoolean().ShouldBeTrue();
        Blockers(delisted, "buyBlockers").ShouldContain("token_not_listed");

        var expired = await AvailabilityForAsync(ngUser, $"AV{suffix.ToUpperInvariant()[..4]}E1");
        expired.GetProperty("buyable").GetBoolean().ShouldBeFalse();
        expired.GetProperty("sellable").GetBoolean().ShouldBeTrue();
        Blockers(expired, "buyBlockers").ShouldContain("disclosure_review_expired");

        var disabledAvailability = await AvailabilityForAsync(ngUser, $"AV{suffix.ToUpperInvariant()[..4]}C1");
        disabledAvailability.GetProperty("buyable").GetBoolean().ShouldBeFalse();
        disabledAvailability.GetProperty("sellable").GetBoolean().ShouldBeFalse();
        Blockers(disabledAvailability, "buyBlockers").ShouldContain("chain_disabled");
        Blockers(disabledAvailability, "sellBlockers").ShouldContain("chain_disabled");

        var draft = await AvailabilityForAsync(ngUser, $"AV{suffix.ToUpperInvariant()[..4]}W1");
        draft.GetProperty("buyable").GetBoolean().ShouldBeFalse();
        draft.GetProperty("sellable").GetBoolean().ShouldBeFalse();

        var usOnly = await AvailabilityForAsync(ngUser, $"AV{suffix.ToUpperInvariant()[..4]}U1");
        usOnly.GetProperty("buyable").GetBoolean().ShouldBeFalse();
        Blockers(usOnly, "buyBlockers").ShouldContain("country_not_allowed");

        var usUser = await VerifiedUserAsync();
        await ComplianceTestUsers.SetCountryAsync(client, usUser, "US");
        (await AvailabilityForAsync(usUser, $"AV{suffix.ToUpperInvariant()[..4]}U1"))
            .GetProperty("buyable").GetBoolean().ShouldBeTrue();

        var suspendedIssuer = await AvailabilityForAsync(ngUser, $"AV{suffix.ToUpperInvariant()[..4]}I1");
        suspendedIssuer.GetProperty("buyable").GetBoolean().ShouldBeFalse();
        Blockers(suspendedIssuer, "buyBlockers").ShouldContain("issuer_not_approved");

        var frUser = await VerifiedUserAsync();
        await ComplianceTestUsers.SetCountryAsync(client, frUser, "FR");
        var frAvailability = await AvailabilityForAsync(frUser, $"AV{suffix.ToUpperInvariant()[..4]}H1");
        frAvailability.GetProperty("buyable").GetBoolean().ShouldBeFalse();
        Blockers(frAvailability, "buyBlockers").ShouldContain("country_not_allowed");

        var noCountryUser = await VerifiedUserAsync();
        var unknownAvailability = await AvailabilityForAsync(noCountryUser, $"AV{suffix.ToUpperInvariant()[..4]}H1");
        unknownAvailability.GetProperty("buyable").GetBoolean().ShouldBeFalse();
        Blockers(unknownAvailability, "buyBlockers").ShouldContain("country_unknown");
    }

    [Fact]
    public async Task Practice_Only_User_Can_Browse_Catalog()
    {
        var suffix = CatalogImportTestData.Suffix();
        var issuerId = $"api-issuer-{suffix}";
        var assetCode = CatalogImportTestData.AssetCode("PRC", suffix);
        await ImportAsync(
            [Chain("eip155:11155111")],
            [Issuer(issuerId)],
            [CatalogImportTestData.Instrument($"api-instr-{suffix}", [
                CatalogImportTestData.Token(issuerId: issuerId, address: CatalogImportTestData.Address(suffix, '1'),
                    assetCode: assetCode, status: "listed"),
            ])]);

        var token = await VerifiedUserAsync();
        var client = Client();

        using var instruments = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/catalog/instruments").Auth(token),
            TestContext.Current.CancellationToken);
        instruments.StatusCode.ShouldBe(HttpStatusCode.OK);

        var tokenId = await CatalogImportTestData.TokenIdByAssetCodeAsync(
            fixture, assetCode, TestContext.Current.CancellationToken);
        using var detail = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/catalog/tokens/{tokenId}").Auth(token),
            TestContext.Current.CancellationToken);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        var availability = (await DataAsync(detail)).GetProperty("availability");
        availability.GetProperty("buyable").GetBoolean().ShouldBeFalse();
        availability.GetProperty("buyBlockers").EnumerateArray()
            .Select(b => b.GetString()).ShouldContain("country_unknown");
    }

    [Fact]
    public async Task Effective_Country_Prefers_Verified_Over_Declared()
    {
        var token = await VerifiedUserAsync();
        var client = Client();
        var userId = UserIdFromToken(token);
        await ComplianceTestUsers.SetCountryAsync(client, token, "NG");

        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var update = new NpgsqlCommand(
            "UPDATE compliance.compliance_profiles SET verified_country = 'US' WHERE user_id = @id", connection))
        {
            update.Parameters.AddWithValue("id", userId);
            (await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var eligibility = scope.ServiceProvider
            .GetRequiredService<AskVest.Modules.Compliance.Contracts.IComplianceEligibility>();
        (await eligibility.GetEffectiveCountryAsync(userId, TestContext.Current.CancellationToken))
            .ShouldBe("US");

        await using (var clear = new NpgsqlCommand(
            "UPDATE compliance.compliance_profiles SET verified_country = NULL WHERE user_id = @id", connection))
        {
            clear.Parameters.AddWithValue("id", userId);
            await clear.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        (await eligibility.GetEffectiveCountryAsync(userId, TestContext.Current.CancellationToken))
            .ShouldBe("NG");
    }
}

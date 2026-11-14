using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Catalog.Import;
using AskVest.Modules.Catalog.Import.Data;
using AskVest.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Catalog;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class CatalogImportTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task First_Import_Creates_Everything_And_Registers_Ledger_Assets()
    {
        var suffix = Suffix();
        var issuerId = $"imp-issuer-{suffix}";
        var instrumentId = $"imp-instr-{suffix}";
        var assetCode = AssetCode("IMP", suffix);
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [CatalogImportTestData.Token(
                issuerId: issuerId,
                address: Address(suffix, 'a'),
                assetCode: assetCode)])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        var report = await ImportAsync(provider);

        report.IsSuccess.ShouldBeTrue(report.Error?.Message);
        report.Value.Tokens.ShouldBe(1);

        var caip19 = await ScalarAsync<string>(
            $"SELECT caip19 FROM catalog.tokens WHERE ledger_asset_code = '{assetCode}'");
        caip19.ShouldStartWith("eip155:11155111/erc20:0x");

        var asset = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT kind, scale FROM ledger.assets WHERE code = '{assetCode}'",
            TestContext.Current.CancellationToken);
        asset.Count.ShouldBe(1);
        asset[0]["kind"].ShouldBe("token");
        asset[0]["scale"].ShouldBe(18L);

        var countries = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT country_code FROM catalog.token_countries tc JOIN catalog.tokens t ON t.id = tc.token_id WHERE t.ledger_asset_code = '{assetCode}' ORDER BY country_code",
            TestContext.Current.CancellationToken);
        countries.Select(c => c["country_code"]).ShouldBe(["NG", "US"]);
    }

    [Fact]
    public async Task External_Transfers_Defaults_To_Not_Supported_And_Stores_The_Value()
    {
        var suffix = Suffix();
        var issuerId = $"xfer-issuer-{suffix}";
        var instrumentId = $"xfer-instr-{suffix}";
        var assetA = AssetCode("XFER", suffix);
        var assetB = AssetCode("XFR2", suffix);
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId,
            [
                CatalogImportTestData.Token(
                    issuerId: issuerId,
                    address: Address(suffix, 'a'),
                    assetCode: assetA,
                    externalTransfers: "allowed"),
                CatalogImportTestData.Token(
                    issuerId: issuerId,
                    address: Address(suffix, 'b'),
                    assetCode: assetB),
            ])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        var report = await ImportAsync(provider);
        report.IsSuccess.ShouldBeTrue(report.Error?.Message);

        var rows = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT external_transfers FROM catalog.tokens WHERE ledger_asset_code IN ('{assetA}', '{assetB}') ORDER BY external_transfers",
            TestContext.Current.CancellationToken);
        rows.Select(row => row["external_transfers"]).ShouldBe(["allowed", "not_supported"]);
    }

    [Fact]
    public async Task Invalid_External_Transfers_Value_Is_A_Problem()
    {
        var suffix = Suffix();
        var issuerId = $"xfer-issuer-{suffix}";
        var instrumentId = $"xfer-instr-{suffix}";
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [CatalogImportTestData.Token(
                issuerId: issuerId,
                address: Address(suffix, 'a'),
                assetCode: AssetCode("XFER", suffix),
                externalTransfers: "sometimes")])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        var report = await ImportAsync(provider);
        report.IsFailure.ShouldBeTrue();
        report.Error!.Message.ShouldContain("external transfers");
    }

    [Fact]
    public async Task Reimport_Of_Unchanged_Data_Writes_Nothing()
    {
        var suffix = Suffix();
        var issuerId = $"idem-issuer-{suffix}";
        var instrumentId = $"idem-instr-{suffix}";
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [CatalogImportTestData.Token(
                address: Address(suffix, 'a'),
                issuerId: issuerId, assetCode: AssetCode("IDEM", suffix))])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        (await ImportAsync(provider)).IsSuccess.ShouldBeTrue();

        var eventsBefore = await ScalarAsync<long>("SELECT count(*) FROM catalog.catalog_events");
        var outboxBefore = await ScalarAsync<long>(
            "SELECT count(*) FROM platform.outbox_messages WHERE type = 'CatalogTokenStatusChanged'");

        (await ImportAsync(provider)).IsSuccess.ShouldBeTrue();

        (await ScalarAsync<long>("SELECT count(*) FROM catalog.catalog_events")).ShouldBe(eventsBefore);
        (await ScalarAsync<long>(
            "SELECT count(*) FROM platform.outbox_messages WHERE type = 'CatalogTokenStatusChanged'"))
            .ShouldBe(outboxBefore);
    }

    [Fact]
    public async Task Broken_Data_Reports_All_Problems_And_Writes_Nothing()
    {
        var suffix = Suffix();
        var issuerId = $"bad-issuer-{suffix}";
        var instrumentId = $"bad-instr-{suffix}";
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId,
            [
                CatalogImportTestData.Token(
                    issuerId: issuerId,
                    address: Address(suffix, 'a'),
                    assetCode: AssetCode("BAD", suffix),
                    decimals: 19,
                    countries: ["X1"],
                    gatewayKey: null),
                CatalogImportTestData.Token(
                    issuerId: "missing-issuer",
                    chainId: "eip155:999",
                    address: Address(suffix, 'b'),
                    assetCode: AssetCode("BAD2", suffix),
                    status: "bogus"),
                CatalogImportTestData.Token(
                    issuerId: issuerId,
                    address: "not-an-address",
                    assetCode: AssetCode("BAD3", suffix),
                    status: "draft"),
            ])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        var result = await ImportAsync(provider);

        result.IsFailure.ShouldBeTrue();
        output.WriteLine(result.Error!.Message);
        var message = result.Error!.Message;
        message.ShouldContain("decimals_above_maximum");
        message.ShouldContain("allowed country 'X1'");
        message.ShouldContain("issuer 'missing-issuer' does not exist");
        message.ShouldContain("chain 'eip155:999' does not exist");
        message.ShouldContain("not valid for this chain");
        message.ShouldContain("status 'bogus' is not valid");

        var tokens = await ScalarAsync<long>(
            $"SELECT count(*) FROM catalog.tokens WHERE instrument_id = '{instrumentId}'");
        tokens.ShouldBe(0);
    }

    [Fact]
    public async Task Token_With_Practice_Prefixed_Asset_Code_Is_Rejected()
    {
        var suffix = Suffix();
        var issuerId = $"pfx-issuer-{suffix}";
        var instrumentId = $"pfx-instr-{suffix}";
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [CatalogImportTestData.Token(
                issuerId: issuerId,
                address: Address(suffix, 'a'),
                assetCode: $"P.{AssetCode("PFX", suffix)}")])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        var result = await ImportAsync(provider);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("reserved practice prefix 'P.'");
    }

    [Theory]
    [InlineData("claimDescription")]
    [InlineData("backingDescription")]
    [InlineData("custodianDescription")]
    [InlineData("redemptionDescription")]
    [InlineData("transferRestrictions")]
    [InlineData("dividendNote")]
    [InlineData("votingNote")]
    [InlineData("disclosureDocumentLink")]
    public async Task Listing_Guard_Fails_When_Mandatory_Disclosure_Is_Removed(string field)
    {
        var suffix = Suffix();
        var issuerId = $"disc-issuer-{suffix}";
        var instrumentId = $"disc-instr-{suffix}";
        var token = BlankField(
            CatalogImportTestData.Token(
                issuerId: issuerId,
                address: Address(suffix, 'a'),
                assetCode: AssetCode("DISC", suffix)),
            field);
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [token])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        var result = await ImportAsync(provider);

        result.IsFailure.ShouldBeTrue();
        var expected = field == "disclosureDocumentLink"
            ? "disclosure document link"
            : $"disclosure '{field}'";
        result.Error!.Message.ShouldContain(expected);
    }

    [Fact]
    public async Task Listing_Guard_Fails_When_Risk_Statements_Are_Too_Few()
    {
        var suffix = Suffix();
        var issuerId = $"risk-issuer-{suffix}";
        var instrumentId = $"risk-instr-{suffix}";
        var token = CatalogImportTestData.Token(
            address: Address(suffix, 'a'),
            issuerId: issuerId, assetCode: AssetCode("RISK", suffix)) with
        { RiskStatements = ["only one"] };
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [token])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        var result = await ImportAsync(provider);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("risk statements");
    }

    [Theory]
    [InlineData("issuer_suspended")]
    [InlineData("chain_disabled")]
    [InlineData("no_countries")]
    [InlineData("no_gateway")]
    [InlineData("review_expired")]
    [InlineData("review_future")]
    [InlineData("instrument_delisted")]
    [InlineData("family_unsupported")]
    public async Task Listing_Guard_Fails_For_Each_Eligibility_Condition(string scenario)
    {
        var suffix = Suffix();
        var issuerId = $"guard-issuer-{suffix}";
        var instrumentId = $"guard-instr-{suffix}";
        var chain = CatalogImportTestData.Chain(
            status: scenario == "chain_disabled" ? "disabled" : "enabled") with
        {
            Family = scenario == "family_unsupported" ? "svm" : "evm",
        };
        var issuer = CatalogImportTestData.Issuer(
            issuerId, status: scenario == "issuer_suspended" ? "suspended" : "approved");
        var instrument = CatalogImportTestData.Instrument(
            instrumentId,
            [CatalogImportTestData.Token(
                issuerId: issuerId,
                address: Address(suffix, 'a'),
                assetCode: AssetCode("GRD", suffix),
                countries: scenario == "no_countries" ? [] : ["US"],
                gatewayKey: scenario == "no_gateway" ? null : "gateway-a",
                reviewedAt: scenario == "review_future"
                    ? DateTimeOffset.Parse("2999-01-01T00:00:00Z")
                    : DateTimeOffset.Parse(CatalogImportTestData.ValidReviewStart),
                reviewExpiresAt: scenario == "review_expired"
                    ? DateTimeOffset.Parse("2020-01-01T00:00:00Z")
                    : DateTimeOffset.Parse(CatalogImportTestData.ValidReviewEnd))]);
        if (scenario == "instrument_delisted")
        {
            instrument = instrument with { Status = "delisted" };
        }

        var folder = await CatalogImportTestData.WriteFolderAsync(
            [chain], [issuer], [instrument], TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        var result = await ImportAsync(provider);
        result.IsFailure.ShouldBeTrue($"scenario {scenario} should fail");
        output.WriteLine(result.Error!.Message);
        result.Error!.Message.ShouldContain("cannot be listed");
    }

    [Fact]
    public async Task Immutable_Fields_Cannot_Change_After_Listing()
    {
        var suffix = Suffix();
        var issuerId = $"imm-issuer-{suffix}";
        var instrumentId = $"imm-instr-{suffix}";
        var assetCode = AssetCode("IMM", suffix);
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [CatalogImportTestData.Token(
                issuerId: issuerId, address: Address(suffix, 'a'), assetCode: assetCode)])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        (await ImportAsync(provider)).IsSuccess.ShouldBeTrue();

        var changed = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [CatalogImportTestData.Token(
                issuerId: issuerId,
                address: Address(suffix, 'c'),
                assetCode: assetCode,
                decimals: 8)])],
            TestContext.Current.CancellationToken);
        await using var secondProvider = CatalogImportTestData.CreateProvider(fixture, changed);
        var result = await ImportAsync(secondProvider);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("cannot change once the token has been listed");
    }

    [Fact]
    public async Task Illegal_Status_Transitions_Are_Rejected()
    {
        var suffix = Suffix();
        var issuerId = $"trans-issuer-{suffix}";
        var instrumentId = $"trans-instr-{suffix}";
        var assetCode = AssetCode("TRN", suffix);
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [CatalogImportTestData.Token(
                issuerId: issuerId, address: Address(suffix, 'a'), assetCode: assetCode, status: "draft")])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        (await ImportAsync(provider)).IsSuccess.ShouldBeTrue();

        var suspended = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [CatalogImportTestData.Token(
                issuerId: issuerId, address: Address(suffix, 'a'), assetCode: assetCode, status: "suspended")])],
            TestContext.Current.CancellationToken);
        await using var secondProvider = CatalogImportTestData.CreateProvider(fixture, suspended);
        var result = await ImportAsync(secondProvider);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("status transition");
    }

    [Fact]
    public async Task Missing_File_Entry_Delists_The_Token()
    {
        var suffix = Suffix();
        var issuerId = $"gone-issuer-{suffix}";
        var instrumentId = $"gone-instr-{suffix}";
        var assetCode = AssetCode("GONE", suffix);
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [CatalogImportTestData.Token(
                issuerId: issuerId, address: Address(suffix, 'a'), assetCode: assetCode)])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        (await ImportAsync(provider)).IsSuccess.ShouldBeTrue();

        var emptied = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId)],
            TestContext.Current.CancellationToken);
        await using var secondProvider = CatalogImportTestData.CreateProvider(fixture, emptied);
        (await ImportAsync(secondProvider)).IsSuccess.ShouldBeTrue();

        var status = await ScalarAsync<string>(
            $"SELECT status FROM catalog.tokens WHERE ledger_asset_code = '{assetCode}'");
        status.ShouldBe("delisted");
    }

    [Fact]
    public async Task Sample_Data_Is_Rejected_In_Production_And_Loaded_Otherwise()
    {
        var repo = CatalogImportTestData.RepoRoot();
        var realPath = Path.Combine(repo, "data", "catalog");
        var samplePath = Path.Combine(repo, "data", "catalog-sample");

        await using var production = CatalogImportTestData.CreateProvider(
            fixture, realPath, samplePath, loadSampleData: true, environmentName: "Production");
        var denied = await ImportAsync(production);
        denied.IsFailure.ShouldBeTrue();
        denied.Error!.Message.ShouldContain("Production");

        await using var development = CatalogImportTestData.CreateProvider(
            fixture, realPath, samplePath, loadSampleData: true, environmentName: "Development");
        var loaded = await ImportAsync(development);
        loaded.IsSuccess.ShouldBeTrue(loaded.Error?.Message);
        loaded.Value.Tokens.ShouldBe(9);
    }

    [Fact]
    public async Task Validate_Only_Succeeds_For_Real_And_Sample_Folders()
    {
        var repo = CatalogImportTestData.RepoRoot();
        await using var provider = CatalogImportTestData.CreateProvider(
            fixture,
            Path.Combine(repo, "data", "catalog"),
            Path.Combine(repo, "data", "catalog-sample"),
            loadSampleData: true,
            environmentName: "Test");

        var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<CatalogImportService>()
            .ValidateAsync(TestContext.Current.CancellationToken);
        await scope.DisposeAsync();

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.ValidateOnly.ShouldBeTrue();
    }

    [Fact]
    public async Task Status_Change_Writes_One_Event_And_One_Outbox_Message()
    {
        var suffix = Suffix();
        var issuerId = $"evt-issuer-{suffix}";
        var instrumentId = $"evt-instr-{suffix}";
        var assetCode = AssetCode("EVT", suffix);
        var folder = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [CatalogImportTestData.Token(
                issuerId: issuerId, address: Address(suffix, 'a'), assetCode: assetCode, status: "draft")])],
            TestContext.Current.CancellationToken);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, folder);
        (await ImportAsync(provider)).IsSuccess.ShouldBeTrue();

        var listed = await CatalogImportTestData.WriteFolderAsync(
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer(issuerId)],
            [CatalogImportTestData.Instrument(instrumentId, [CatalogImportTestData.Token(
                issuerId: issuerId, address: Address(suffix, 'a'), assetCode: assetCode, status: "listed")])],
            TestContext.Current.CancellationToken);
        await using var secondProvider = CatalogImportTestData.CreateProvider(fixture, listed);
        (await ImportAsync(secondProvider)).IsSuccess.ShouldBeTrue();

        var tokenId = await ScalarAsync<string>(
            $"SELECT id::text FROM catalog.tokens WHERE ledger_asset_code = '{assetCode}'");
        var events = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT event_type FROM catalog.catalog_events WHERE subject_type = 'token' AND subject_id = '{tokenId}' ORDER BY occurred_at",
            TestContext.Current.CancellationToken);
        events.Select(e => e["event_type"]).ShouldBe(["imported", "listed"]);

        var outbox = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT payload FROM platform.outbox_messages WHERE type = 'CatalogTokenStatusChanged' AND payload::text LIKE '%{tokenId}%'",
            TestContext.Current.CancellationToken);
        outbox.Count.ShouldBe(1);
        outbox[0]["payload"]!.ToString()!.ShouldNotContain(assetCode);
    }

    private static async Task<Result<CatalogImportReport>> ImportAsync(ServiceProvider provider)
    {
        var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<CatalogImportService>()
            .ImportAsync(TestContext.Current.CancellationToken);
        await scope.DisposeAsync();
        return result;
    }

    private static TokenData BlankField(TokenData token, string field) => field switch
    {
        "claimDescription" => token with { ClaimDescription = "" },
        "backingDescription" => token with { BackingDescription = "" },
        "custodianDescription" => token with { CustodianDescription = "" },
        "redemptionDescription" => token with { RedemptionDescription = "" },
        "transferRestrictions" => token with { TransferRestrictions = "" },
        "dividendNote" => token with { DividendNote = "" },
        "votingNote" => token with { VotingNote = "" },
        "disclosureDocumentLink" => token with { DisclosureDocumentLink = "" },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown disclosure field."),
    };

    private static string Suffix() => Guid.CreateVersion7().ToString("N")[^8..];

    private static string AssetCode(string prefix, string suffix)
        => $"{prefix}{suffix.ToUpperInvariant()[..6]}";

    private static string Address(string suffix, char fill)
        => $"0x{suffix}{new string(fill, 32)}";

    private async Task<T> ScalarAsync<T>(string sql)
        => await Database.ExecuteScalarAsync<T>(fixture.OwnerConnectionString, sql, TestContext.Current.CancellationToken);
}

using System.Text.Json;
using AskVest.Modules.Catalog;
using AskVest.Modules.Catalog.Configuration;
using AskVest.Modules.Catalog.Import;
using AskVest.Modules.Catalog.Import.Data;
using AskVest.Modules.Compliance;
using AskVest.Modules.Identity;
using AskVest.Modules.Ledger;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace AskVest.IntegrationTests.Catalog;

internal static class CatalogImportTestData
{
    public const string ValidReviewStart = "2026-01-15T00:00:00+00:00";
    public const string ValidReviewEnd = "2027-01-15T00:00:00+00:00";

    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AskVest.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    public static async Task<string> WriteFolderAsync(
        IReadOnlyList<ChainData> chains,
        IReadOnlyList<IssuerData> issuers,
        IReadOnlyList<InstrumentData> instruments,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "askvest-catalog-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "chains.json"),
            JsonSerializer.Serialize(new { version = "test-1", chains }, Json), cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "issuers.json"),
            JsonSerializer.Serialize(new { version = "test-1", issuers }, Json), cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "instruments.json"),
            JsonSerializer.Serialize(new { version = "test-1", instruments }, Json), cancellationToken);
        return directory;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static ChainData Chain(string id = "eip155:11155111", string status = "enabled", string family = "evm")
        => new()
        {
            Id = id,
            Family = family,
            Name = "Sample Chain",
            NativeAssetSymbol = "ETH",
            NativeDecimals = 18,
            ExplorerLinkTemplate = "https://example.com/address/{address}",
            RequiredConfirmations = 1,
            Status = status,
        };

    public static IssuerData Issuer(string id = "issuer-a", string status = "approved")
        => new()
        {
            Id = id,
            LegalName = "Sample Issuer Ltd",
            Jurisdiction = "US",
            Website = "https://issuer.example.com",
            RegulatoryStatus = "sample",
            LegalDocumentLinks = ["https://issuer.example.com/legal"],
            Status = status,
            ReviewedAt = DateTimeOffset.Parse(ValidReviewStart),
            ReviewExpiresAt = DateTimeOffset.Parse(ValidReviewEnd),
        };

    public static InstrumentData Instrument(string id = "instr-a", IEnumerable<TokenData>? tokens = null)
        => new()
        {
            Id = id,
            Kind = "stock",
            Name = "Sample Instrument",
            Symbol = "SMP",
            CountryOfListing = "US",
            QuoteCurrency = "USD",
            Status = "active",
            Tokens = tokens?.ToList() ?? [],
        };

    public static TokenData Token(
        string issuerId = "issuer-a",
        string chainId = "eip155:11155111",
        string address = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01",
        string status = "listed",
        string assetCode = "SMPAST1",
        bool everListed = false,
        string? gatewayKey = "gateway-a",
        int decimals = 18,
        string[]? countries = null,
        DateTimeOffset? reviewedAt = null,
        DateTimeOffset? reviewExpiresAt = null,
        string claimType = "direct_share_claim",
        string dividendPolicy = "distributed")
        => new()
        {
            IssuerId = issuerId,
            ChainId = chainId,
            Standard = "erc20",
            Address = address,
            Symbol = "SMP",
            Decimals = decimals,
            LedgerAssetCode = assetCode,
            Status = status,
            EverListed = everListed,
            GatewayKey = gatewayKey,
            Schedule = new TradingScheduleData { Kind = "always" },
            ClaimType = claimType,
            ClaimDescription = "Holders claim a notional share in the sample issuer.",
            BackingDescription = "Each token is backed one to one by a custodied share.",
            CustodianDescription = "Shares are held by a sample custodian.",
            RedemptionDescription = "Holders may redeem tokens for the underlying share.",
            DividendPolicy = dividendPolicy,
            DividendNote = "Dividends pass through to holders.",
            VotingRights = true,
            VotingNote = "Holders may vote through the sample portal.",
            TransferRestrictions = "Transfers are limited to allowlisted wallets.",
            IssuerFees = ["0.10% annual fee"],
            NoIssuerFees = false,
            RiskStatements = ["Sample risk one.", "Sample risk two.", "Sample risk three."],
            DisclosureDocumentLink = "https://example.com/disclosures/token",
            AllowedCountries = countries?.ToList() ?? ["US", "NG"],
            ReviewedAt = reviewedAt ?? DateTimeOffset.Parse(ValidReviewStart),
            ReviewExpiresAt = reviewExpiresAt ?? DateTimeOffset.Parse(ValidReviewEnd),
            ReviewerLabel = "sample-reviewer",
        };

    public static string Suffix() => Guid.CreateVersion7().ToString("N")[^8..];

    public static string AssetCode(string prefix, string suffix)
        => $"{prefix}{suffix.ToUpperInvariant()[..6]}";

    public static string Address(string suffix, char fill)
        => $"0x{suffix}{new string(fill, 32)}";

    public static async Task<Guid> TokenIdByAssetCodeAsync(
        Infrastructure.PostgresFixture fixture,
        string assetCode,
        CancellationToken cancellationToken)
    {
        await using var connection = new Npgsql.NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT id FROM catalog.tokens WHERE ledger_asset_code = @code", connection);
        command.Parameters.AddWithValue("code", assetCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        value.ShouldNotBeNull($"no catalog token with asset code '{assetCode}'");
        return (Guid)value!;
    }

    public static async Task<Result<CatalogImportReport>> ImportFolderAsync(
        Infrastructure.PostgresFixture fixture,
        IReadOnlyList<ChainData> chains,
        IReadOnlyList<IssuerData> issuers,
        IReadOnlyList<InstrumentData> instruments,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        var folder = await WriteFolderAsync(chains, issuers, instruments, cancellationToken);
        await using var provider = CreateProvider(fixture, folder, timeProvider: timeProvider);
        var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<CatalogImportService>()
            .ImportAsync(cancellationToken);
        await scope.DisposeAsync();
        return result;
    }

    public static ServiceProvider CreateProvider(
        Infrastructure.PostgresFixture fixture,
        string dataPath,
        string? sampleDataPath = null,
        bool loadSampleData = false,
        string environmentName = "Test",
        TimeProvider? timeProvider = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = fixture.OwnerConnectionString,
                [$"{CatalogOptions.SectionName}:DataPath"] = dataPath,
                [$"{CatalogOptions.SectionName}:SampleDataPath"] = sampleDataPath,
                [$"{CatalogOptions.SectionName}:LoadSampleData"] = loadSampleData ? "true" : "false",
                ["Compliance:Kyc:WebhookSecret"] = "test-webhook-secret-0123456789abcdef0123456789",
                ["Compliance:RealMoneyCountries:0"] = "US",
                ["Compliance:RealMoneyCountries:1"] = "NG",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));
        services.AddPlatformPersistence(configuration);
        services.AddIdentityModule(configuration);
        services.AddComplianceModule(configuration);
        services.AddLedgerModule(configuration);
        services.AddCatalogModule(configuration);
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "AskVest.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

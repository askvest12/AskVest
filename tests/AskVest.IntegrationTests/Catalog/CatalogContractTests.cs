using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Catalog.Import.Data;
using AskVest.Modules.Catalog.Services;
using AskVest.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Catalog;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class CatalogContractTests(PostgresFixture fixture)
{
    private static string UnusedDataPath()
        => Path.Combine(Path.GetTempPath(), "askvest-unused-" + Guid.CreateVersion7().ToString("N"));

    private async Task ImportAsync(
        IReadOnlyList<ChainData> chains,
        IReadOnlyList<IssuerData> issuers,
        IReadOnlyList<InstrumentData> instruments,
        TimeProvider? timeProvider = null)
    {
        var result = await CatalogImportTestData.ImportFolderAsync(
            fixture, chains, issuers, instruments, TestContext.Current.CancellationToken, timeProvider);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    private static (ChainData Chain, IssuerData Issuer, InstrumentData Instrument, TokenData Token) Minimal(
        string suffix, string chainId = "eip155:11155111", string assetPrefix = "CTR")
    {
        var issuerId = $"ctr-issuer-{suffix}";
        var issuer = CatalogImportTestData.Issuer(issuerId);
        var token = CatalogImportTestData.Token(
            issuerId: issuerId,
            chainId: chainId,
            address: CatalogImportTestData.Address(suffix, '1'),
            assetCode: CatalogImportTestData.AssetCode(assetPrefix, suffix),
            status: "listed");
        var instrument = CatalogImportTestData.Instrument($"ctr-instr-{suffix}", [token]);
        return (CatalogImportTestData.Chain(chainId), issuer, instrument, token);
    }

    [Fact]
    public async Task FindTokenAsync_Returns_Contract_Projection()
    {
        var suffix = CatalogImportTestData.Suffix();
        var (chain, issuer, instrument, token) = Minimal(suffix);
        await ImportAsync([chain], [issuer], [instrument]);
        await using var provider = CatalogImportTestData.CreateProvider(fixture, UnusedDataPath());
        await using var scope = provider.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalog>();

        var found = await catalog.FindTokenByAssetCodeAsync(
            token.LedgerAssetCode.ToLowerInvariant(), TestContext.Current.CancellationToken);
        found.ShouldNotBeNull();
        found!.Status.ShouldBe("listed");
        found.LedgerAssetCode.ShouldBe(token.LedgerAssetCode);
        found.Caip19.ShouldStartWith("eip155:11155111/");
        found.ChainId.ShouldBe("eip155:11155111");
        found.Decimals.ShouldBe(18);

        (await catalog.FindTokenAsync(found.Id, TestContext.Current.CancellationToken))
            .ShouldBe(found);
        (await catalog.FindTokenAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await catalog.FindTokenByAssetCodeAsync("NOPE99", TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task FindInstrument_And_Chain_Return_Contract_Projections()
    {
        var suffix = CatalogImportTestData.Suffix();
        var (chain, issuer, instrument, _) = Minimal(suffix);
        await ImportAsync([chain], [issuer], [instrument]);
        await using var provider = CatalogImportTestData.CreateProvider(fixture, UnusedDataPath());
        await using var scope = provider.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalog>();

        var found = await catalog.FindInstrumentAsync(instrument.Id, TestContext.Current.CancellationToken);
        found.ShouldNotBeNull();
        found!.Kind.ShouldBe("stock");
        found.Symbol.ShouldBe(instrument.Symbol);
        found.Status.ShouldBe("active");
        (await catalog.FindInstrumentAsync("missing", TestContext.Current.CancellationToken)).ShouldBeNull();

        var foundChain = await catalog.FindChainAsync(chain.Id, TestContext.Current.CancellationToken);
        foundChain.ShouldNotBeNull();
        foundChain!.Family.ShouldBe("evm");
        foundChain.Status.ShouldBe("enabled");
        (await catalog.FindChainAsync("eip155:999999", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ListListedTokensForChain_Returns_Only_Listed()
    {
        var suffix = CatalogImportTestData.Suffix();
        var chainId = "eip155:970001";
        var (chain, issuer, instrument, token) = Minimal(suffix, chainId, "LST");
        await ImportAsync([chain], [issuer], [instrument]);
        await using var provider = CatalogImportTestData.CreateProvider(fixture, UnusedDataPath());
        await using var scope = provider.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalog>();

        var listed = await catalog.ListListedTokensForChainAsync(chainId, TestContext.Current.CancellationToken);
        listed.Count.ShouldBe(1);
        listed[0].LedgerAssetCode.ShouldBe(token.LedgerAssetCode);
        listed[0].Status.ShouldBe("listed");

        (await catalog.ListListedTokensForChainAsync("eip155:17000", TestContext.Current.CancellationToken))
            .ShouldNotContain(t => t.LedgerAssetCode == token.LedgerAssetCode);
    }

    [Fact]
    public async Task ValidateAddress_Canonicalizes_And_Rejects()
    {
        var suffix = CatalogImportTestData.Suffix();
        var (chain, issuer, instrument, token) = Minimal(suffix);
        await ImportAsync([chain], [issuer], [instrument]);
        await using var provider = CatalogImportTestData.CreateProvider(fixture, UnusedDataPath());
        await using var scope = provider.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalog>();
        var stored = await catalog.FindTokenByAssetCodeAsync(
            token.LedgerAssetCode, TestContext.Current.CancellationToken);
        stored.ShouldNotBeNull();

        var canonical = await catalog.ValidateAddressAsync(
            chain.Id, token.Address.ToLowerInvariant(), TestContext.Current.CancellationToken);
        canonical.IsSuccess.ShouldBeTrue();
        canonical.Value.ShouldBe(stored!.CanonicalAddress);

        var invalid = await catalog.ValidateAddressAsync(chain.Id, "0x1234", TestContext.Current.CancellationToken);
        invalid.IsFailure.ShouldBeTrue();
        invalid.Error!.Code.ShouldBe(ErrorCodes.CatalogAddressInvalid);

        var missingChain = await catalog.ValidateAddressAsync(
            "eip155:999999", token.Address.ToLowerInvariant(), TestContext.Current.CancellationToken);
        missingChain.IsFailure.ShouldBeTrue();
        missingChain.Error!.Code.ShouldBe(ErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task GetAvailability_Returns_Null_For_Unknown_Token()
    {
        await using var provider = CatalogImportTestData.CreateProvider(fixture, UnusedDataPath());
        await using var scope = provider.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalog>();

        (await catalog.GetAvailabilityAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Availability_Flips_When_Clock_Passes_Review_Expiry()
    {
        var suffix = CatalogImportTestData.Suffix();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var issuerId = $"ctr-issuer-{suffix}";
        var expiring = CatalogImportTestData.Token(
            issuerId: issuerId,
            address: CatalogImportTestData.Address(suffix, '1'),
            assetCode: CatalogImportTestData.AssetCode("EXP", suffix),
            status: "listed",
            reviewedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            reviewExpiresAt: new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero));
        var instrument = CatalogImportTestData.Instrument($"ctr-instr-{suffix}", [expiring]);
        await ImportAsync(
            [CatalogImportTestData.Chain("eip155:11155111")],
            [CatalogImportTestData.Issuer(issuerId)],
            [instrument],
            clock);

        await using var provider = CatalogImportTestData.CreateProvider(fixture, UnusedDataPath(), timeProvider: clock);
        await using var scope = provider.CreateAsyncScope();
        var availability = scope.ServiceProvider.GetRequiredService<CatalogAvailabilityService>();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalog>();
        var token = await catalog.FindTokenByAssetCodeAsync(
            expiring.LedgerAssetCode, TestContext.Current.CancellationToken);
        token.ShouldNotBeNull();
        var userId = Guid.CreateVersion7();

        var before = await availability.GetAsync(userId, token!.Id, TestContext.Current.CancellationToken);
        before.ShouldNotBeNull();
        before!.BuyBlockers.ShouldNotContain("disclosure_review_expired");

        clock.Advance(TimeSpan.FromDays(60));
        var after = await availability.GetAsync(userId, token.Id, TestContext.Current.CancellationToken);
        after.ShouldNotBeNull();
        after!.BuyBlockers.ShouldContain("disclosure_review_expired");
        after.Sellable.ShouldBeTrue();
    }
}

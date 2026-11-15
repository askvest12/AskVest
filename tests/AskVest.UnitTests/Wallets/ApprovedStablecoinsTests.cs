using AskVest.Modules.Wallets.Configuration;
using AskVest.Modules.Wallets.Services;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Wallets;

public sealed class ApprovedStablecoinsTests
{
    [Fact]
    public void TryResolve_Matches_Chain_And_Canonical_Contract()
    {
        var registry = new ApprovedStablecoins();
        registry.Replace(
        [
            new ApprovedStablecoinOption
            {
                ChainId = "eip155:1",
                Contract = "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48",
                LedgerAssetCode = "USDC",
                Decimals = 6,
            },
        ]);

        registry.TryResolve(
                "eip155:1", "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48", out var hit)
            .ShouldBeTrue();
        hit!.LedgerAssetCode.ShouldBe("USDC");
        registry.TryResolve("eip155:1", "0xother", out _).ShouldBeFalse();
        registry.TryResolve("eip155:137", "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48", out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void Entries_Are_Aggregated_Per_Asset_Code()
    {
        var registry = new ApprovedStablecoins();
        registry.Replace(
        [
            new ApprovedStablecoinOption { ChainId = "eip155:1", Contract = "0xaaa", LedgerAssetCode = "USDC", Decimals = 6 },
            new ApprovedStablecoinOption { ChainId = "eip155:137", Contract = "0xbbb", LedgerAssetCode = "USDC", Decimals = 6 },
            new ApprovedStablecoinOption { ChainId = "eip155:1", Contract = "0xccc", LedgerAssetCode = "EURC", Decimals = 6 },
        ]);

        registry.Entries.Count.ShouldBe(3);
        registry.Entries.Select(e => e.LedgerAssetCode).Distinct().Count().ShouldBe(2);
    }

    [Theory]
    [InlineData("", "0xabc", "USDC", 6)]
    [InlineData("eip155:1", "", "USDC", 6)]
    [InlineData("eip155:1", "0xabc", "", 6)]
    [InlineData("eip155:1", "0xabc", "USDC", 19)]
    public void Invalid_Entries_Fail_Options_Validation(
        string chainId, string contract, string assetCode, int decimals)
    {
        var options = new WalletsOptions();
        options.ApprovedStablecoins.Add(new ApprovedStablecoinOption
        {
            ChainId = chainId,
            Contract = contract,
            LedgerAssetCode = assetCode,
            Decimals = decimals,
        });

        var result = WalletsOptions.Validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("ApprovedStablecoins");
    }

    [Fact]
    public void Duplicate_Chain_And_Contract_Fails_Validation()
    {
        var options = new WalletsOptions();
        options.ApprovedStablecoins.Add(new ApprovedStablecoinOption
        {
            ChainId = "eip155:1",
            Contract = "0xabc",
            LedgerAssetCode = "USDC",
            Decimals = 6,
        });
        options.ApprovedStablecoins.Add(new ApprovedStablecoinOption
        {
            ChainId = "eip155:1",
            Contract = "0xabc",
            LedgerAssetCode = "USDT",
            Decimals = 6,
        });

        var result = WalletsOptions.Validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("duplicate");
    }

    [Fact]
    public void Valid_Entry_Passes_Options_Validation()
    {
        var options = new WalletsOptions();
        options.ApprovedStablecoins.Add(new ApprovedStablecoinOption
        {
            ChainId = "eip155:1",
            Contract = "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48",
            LedgerAssetCode = "USDC",
            Decimals = 6,
            DustMinimum = 1m,
        });

        WalletsOptions.Validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }
}

using AskVest.Modules.Catalog.Chains;
using AskVest.Modules.Catalog.Chains.Evm;
using AskVest.SharedKernel;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Catalog;

public sealed class EvmChainFamilyHandlerTests
{
    private readonly EvmChainFamilyHandler _handler = new();

    private const string LowercaseAddress = "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed";
    private const string ChecksumAddress = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";
    private const string ChainId = "eip155:1";

    [Fact]
    public void Family_Is_Evm()
    {
        _handler.Family.ShouldBe("evm");
    }

    [Theory]
    [InlineData("eip155:1")]
    [InlineData("eip155:11155111")]
    [InlineData("eip155:8453")]
    public void Valid_Eip155_Chain_Ids_Are_Accepted(string chainId)
    {
        _handler.IsValidChainId(chainId).ShouldBeTrue();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("eip155:")]
    [InlineData("eip155:abc")]
    [InlineData("eip155:0")]
    [InlineData("eip155:-1")]
    [InlineData("eip155:1:extra")]
    [InlineData("")]
    public void Invalid_Chain_Ids_Are_Rejected(string chainId)
    {
        _handler.IsValidChainId(chainId).ShouldBeFalse();
    }

    [Fact]
    public void Lowercase_Address_Canonicalizes_To_Checksum()
    {
        var result = _handler.CanonicalizeAddress(ChainId, LowercaseAddress);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(ChecksumAddress);
    }

    [Fact]
    public void Uppercase_Address_Canonicalizes_To_Checksum()
    {
        var result = _handler.CanonicalizeAddress(ChainId, LowercaseAddress.ToUpperInvariant());
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(ChecksumAddress);
    }

    [Fact]
    public void Valid_Checksum_Address_Is_Accepted()
    {
        var result = _handler.CanonicalizeAddress(ChainId, ChecksumAddress);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(ChecksumAddress);
    }

    [Fact]
    public void Bad_Checksum_Is_Rejected()
    {
        var tampered = ChecksumAddress[..^1] + (ChecksumAddress[^1] == 'd' ? 'e' : 'd');
        var result = _handler.CanonicalizeAddress(ChainId, tampered);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCodes.CatalogAddressInvalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("0x123")]
    [InlineData("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beae")]
    [InlineData("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed00")]
    [InlineData("5aaeb6053f3e94c9b9a09f33669435e7ef1beaed")]
    [InlineData("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beag")]
    public void Malformed_Addresses_Are_Rejected(string address)
    {
        var result = _handler.CanonicalizeAddress(ChainId, address);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCodes.CatalogAddressInvalid);
    }

    [Fact]
    public void Invalid_Chain_Id_Fails_Address_Validation()
    {
        var result = _handler.CanonicalizeAddress("solana:mainnet", LowercaseAddress);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCodes.CatalogAddressInvalid);
    }

    [Fact]
    public void Asset_Id_Is_Caip19()
    {
        var assetId = _handler.BuildAssetId("eip155:11155111", "ERC20", ChecksumAddress);
        assetId.ShouldBe($"eip155:11155111/erc20:{ChecksumAddress}");
    }

    [Fact]
    public void Explorer_Link_Substitutes_Address()
    {
        var link = _handler.BuildExplorerLink(
            ChainId, ChecksumAddress, "https://etherscan.io/address/{address}");
        link.ShouldBe($"https://etherscan.io/address/{ChecksumAddress}");
    }

    [Fact]
    public void Registry_Resolves_Evm_And_Rejects_Unknown_Families()
    {
        var registry = new ChainFamilyRegistry([_handler]);
        registry.TryGet("evm", out var handler).ShouldBeTrue();
        handler.ShouldBeSameAs(_handler);
        registry.TryGet("svm", out var missing).ShouldBeFalse();
        missing.ShouldBeNull();
    }
}

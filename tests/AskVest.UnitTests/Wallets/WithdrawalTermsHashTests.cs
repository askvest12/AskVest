using AskVest.Modules.Wallets.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Wallets;

public sealed class WithdrawalTermsHashTests
{
    private static readonly Guid WithdrawalId = Guid.Parse("0199f3a2-0000-7000-8000-000000000001");
    private static readonly Guid TokenId = Guid.Parse("0199f3a2-0000-7000-8000-000000000002");
    private static readonly Guid AddressId = Guid.Parse("0199f3a2-0000-7000-8000-000000000003");
    private static readonly DateTimeOffset ExpiresAt = new(2026, 1, 1, 0, 1, 0, TimeSpan.Zero);

    private static string Hash(
        Guid? withdrawalId = null,
        Guid? tokenId = null,
        Guid? addressId = null,
        string? chainId = null,
        string? destination = null,
        decimal? gross = null,
        decimal? fee = null,
        decimal? net = null,
        DateTimeOffset? expiresAt = null)
        => WithdrawalTermsHash.Compute(
            withdrawalId ?? WithdrawalId,
            tokenId ?? TokenId,
            addressId ?? AddressId,
            chainId ?? "eip155:11155111",
            destination ?? "fake-addr-1",
            gross ?? 10m,
            fee ?? 0.5m,
            net ?? 9.5m,
            expiresAt ?? ExpiresAt);

    [Fact]
    public void Same_Terms_Produce_The_Same_Hash()
        => Hash().ShouldBe(Hash());

    [Theory]
    [InlineData("withdrawalId")]
    [InlineData("tokenId")]
    [InlineData("addressId")]
    [InlineData("chainId")]
    [InlineData("destination")]
    [InlineData("gross")]
    [InlineData("fee")]
    [InlineData("net")]
    [InlineData("expiresAt")]
    public void Any_Term_Change_Changes_The_Hash(string term)
    {
        var baseline = Hash();
        var changed = term switch
        {
            "withdrawalId" => Hash(withdrawalId: Guid.CreateVersion7()),
            "tokenId" => Hash(tokenId: Guid.CreateVersion7()),
            "addressId" => Hash(addressId: Guid.CreateVersion7()),
            "chainId" => Hash(chainId: "eip155:17000"),
            "destination" => Hash(destination: "fake-addr-2"),
            "gross" => Hash(gross: 11m),
            "fee" => Hash(fee: 0.6m),
            "net" => Hash(net: 9.4m),
            _ => Hash(expiresAt: ExpiresAt.AddSeconds(1)),
        };
        changed.ShouldNotBe(baseline);
    }

    [Fact]
    public void Hash_Is_Lowercase_Hex_Of_32_Bytes()
    {
        var hash = Hash();
        hash.ShouldMatch("^[0-9a-f]{64}$");
    }
}

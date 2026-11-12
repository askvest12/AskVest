using AskVest.Modules.Trading.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Trading;

public sealed class TermsHashTests
{
    private static readonly DateTimeOffset ExpiresAt = new(2026, 11, 12, 12, 0, 30, TimeSpan.Zero);
    private static readonly Guid QuoteId = Guid.Parse("019a0000-0000-7000-8000-000000000001");
    private static readonly Guid TokenId = Guid.Parse("019a0000-0000-7000-8000-000000000002");

    private static string Hash(
        OrderSide side = OrderSide.Buy,
        decimal quantity = 1.5m,
        decimal price = 100m,
        decimal gross = 150m,
        decimal ourFee = 0m,
        decimal gatewayFee = 0.5m,
        decimal total = 150.5m,
        string settlementAsset = "USD",
        DateTimeOffset? expiresAt = null)
        => TermsHash.Compute(
            QuoteId, TokenId, side, quantity, price, gross, ourFee, gatewayFee, total,
            settlementAsset, expiresAt ?? ExpiresAt);

    [Fact]
    public void Hash_Is_Stable_For_Identical_Terms()
    {
        Hash().ShouldBe(Hash());
        Hash().ShouldNotBeNullOrWhiteSpace();
        Hash().Length.ShouldBe(64);
    }

    [Theory]
    [InlineData(OrderSide.Sell)]
    public void Hash_Changes_When_Side_Changes(OrderSide side)
        => Hash(side: side).ShouldNotBe(Hash());

    [Fact]
    public void Hash_Changes_When_Any_Term_Changes()
    {
        var baseline = Hash();
        Hash(quantity: 1.6m).ShouldNotBe(baseline);
        Hash(price: 101m).ShouldNotBe(baseline);
        Hash(gross: 151m).ShouldNotBe(baseline);
        Hash(ourFee: 1m).ShouldNotBe(baseline);
        Hash(gatewayFee: 0.6m).ShouldNotBe(baseline);
        Hash(total: 151.6m).ShouldNotBe(baseline);
        Hash(settlementAsset: "USDC").ShouldNotBe(baseline);
        Hash(expiresAt: ExpiresAt.AddSeconds(1)).ShouldNotBe(baseline);
    }
}

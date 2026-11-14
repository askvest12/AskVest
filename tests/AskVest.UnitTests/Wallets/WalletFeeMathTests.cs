using AskVest.Modules.Wallets.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Wallets;

public sealed class WalletFeeMathTests
{
    private static readonly Guid TokenId = Guid.Parse("0199f3a2-0000-7000-8000-00000000000a");

    [Fact]
    public void Configured_Fee_Is_Subtracted_From_Gross()
    {
        var fees = new Dictionary<string, decimal> { [TokenId.ToString("D")] = 0.25m };
        WalletFeeMath.FeeForToken(fees, TokenId).ShouldBe(0.25m);
        WalletFeeMath.NetQuantity(10m, 0.25m, 18).ShouldBe(9.75m);
    }

    [Fact]
    public void Missing_Token_Pays_No_Fee()
    {
        WalletFeeMath.FeeForToken(new Dictionary<string, decimal>(), TokenId).ShouldBe(0m);
        WalletFeeMath.NetQuantity(10m, 0m, 18).ShouldBe(10m);
    }

    [Fact]
    public void Fee_Rounds_Down_To_Token_Decimals()
    {
        // A fee with more precision than the token supports never overcharges.
        WalletFeeMath.NetQuantity(1m, 0.123456789m, 6).ShouldBe(0.876544m);
    }

    [Fact]
    public void Fee_Larger_Than_Gross_Gives_Nonpositive_Net()
        => WalletFeeMath.NetQuantity(1m, 2m, 18).ShouldBe(-1m);
}

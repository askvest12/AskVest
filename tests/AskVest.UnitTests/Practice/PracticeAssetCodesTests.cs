using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Practice.Services;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Practice;

public sealed class PracticeAssetCodesTests
{
    private static CatalogInstrument Instrument(string symbol)
        => new("instr-1", "stock", "Name", symbol, null, null, "US", "USD", null, "active");

    [Theory]
    [InlineData("NVDA", "P.NVDA")]
    [InlineData("brk.b", "P.BRK.B")]
    [InlineData("AAPL", "P.AAPL")]
    public void Valid_Symbol_Forms_P_Prefixed_Code(string symbol, string expected)
    {
        PracticeAssetCodes.TryCreateCode(Instrument(symbol), out var code).ShouldBeTrue();
        code.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BAD SYMBOL")]
    [InlineData("ACME/US")]
    [InlineData("ABCDEFGHIJKLMNOPQ")]
    public void Invalid_Symbols_Cannot_Form_A_Code(string symbol)
    {
        PracticeAssetCodes.TryCreateCode(Instrument(symbol), out var code).ShouldBeFalse();
        code.ShouldBeEmpty();
    }

    [Fact]
    public void Symbol_At_The_Length_Limit_Still_Fits()
    {
        // "P." plus 14 characters stays inside the 16 character ledger limit.
        PracticeAssetCodes.TryCreateCode(Instrument("ABCDEFGHIJKLMN"), out var code).ShouldBeTrue();
        code.Length.ShouldBe(16);
    }
}

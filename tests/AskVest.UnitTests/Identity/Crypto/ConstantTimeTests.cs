using AskVest.Modules.Identity.Crypto;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public sealed class ConstantTimeTests
{
    [Fact]
    public void Equal_Strings_Are_True()
    {
        ConstantTime.Equals("abc", "abc").ShouldBeTrue();
        ConstantTime.Equals("", "").ShouldBeTrue();
    }

    [Fact]
    public void Different_Strings_Are_False()
    {
        ConstantTime.Equals("abc", "abd").ShouldBeFalse();
        ConstantTime.Equals("abc", "abcd").ShouldBeFalse();
        ConstantTime.Equals("", "a").ShouldBeFalse();
    }

    [Fact]
    public void Nulls_Only_Equal_To_Nulls()
    {
        ConstantTime.Equals((string?)null, (string?)null).ShouldBeTrue();
        ConstantTime.Equals("a", null).ShouldBeFalse();
        ConstantTime.Equals(null, "a").ShouldBeFalse();
    }

    [Fact]
    public void Byte_Arrays()
    {
        ConstantTime.Equals(new byte[] { 1, 2 }, new byte[] { 1, 2 }).ShouldBeTrue();
        ConstantTime.Equals(new byte[] { 1, 2 }, new byte[] { 1, 3 }).ShouldBeFalse();
        ConstantTime.Equals(new byte[] { 1 }, new byte[] { 1, 2 }).ShouldBeFalse();
        ConstantTime.Equals((byte[]?)null, (byte[]?)null).ShouldBeTrue();
        ConstantTime.Equals(new byte[] { 1 }, (byte[]?)null).ShouldBeFalse();
    }

    [Fact]
    public void Unicode_Compared_By_Bytes()
    {
        ConstantTime.Equals("café", "café").ShouldBeTrue();
        ConstantTime.Equals("café", "cafe").ShouldBeFalse();
    }
}

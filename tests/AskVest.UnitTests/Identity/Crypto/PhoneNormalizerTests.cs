using AskVest.Modules.Identity.Crypto;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public sealed class PhoneNormalizerTests
{
    [Theory]
    [InlineData("+2348012345678", "+2348012345678")] // Nigeria
    [InlineData("08012345678", "+2348012345678")] // NG local with default region
    [InlineData("+447911123456", "+447911123456")] // UK
    [InlineData("+33142686800", "+33142686800")] // France
    [InlineData("+254712345678", "+254712345678")] // Kenya
    [InlineData("+27123456789", "+27123456789")] // South Africa
    [InlineData("+233241234567", "+233241234567")] // Ghana
    public void Valid_Numbers_Normalize_To_E164(string input, string expected)
    {
        var result = PhoneNormalizer.Normalize(input, "NG");
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-phone")]
    [InlineData("12345")]
    [InlineData("+99999999999999999999")]
    public void Invalid_Numbers_Fail(string? input)
    {
        PhoneNormalizer.Normalize(input, "NG").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Local_Number_Without_Region_Fails()
    {
        // "08012345678" is ambiguous without a default region.
        PhoneNormalizer.Normalize("08012345678", null).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Formatting_Is_Stripped()
    {
        var result = PhoneNormalizer.Normalize("+234 801 234 5678", "NG");
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("+2348012345678");
    }
}

using AskVest.Modules.Identity.Crypto;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public sealed class EmailNormalizerTests
{
    [Theory]
    [InlineData("User@Example.COM", "user@example.com")]
    [InlineData("  spaced@out.com  ", "spaced@out.com")]
    [InlineData("first.last+tag@sub.domain.co.uk", "first.last+tag@sub.domain.co.uk")]
    public void Valid_Emails_Normalize(string input, string expected)
    {
        var result = EmailNormalizer.Normalize(input);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign.com")]
    [InlineData("two@@ats.com")]
    [InlineData("@nodomain.com")]
    [InlineData("local@")]
    [InlineData("local@nodot")]
    [InlineData("local@-bad.com")]
    [InlineData("local@bad-.com")]
    [InlineData("local@.com")]
    [InlineData("local part@domain.com")]
    [InlineData("a@b.c")] // TLD too short
    public void Invalid_Emails_Fail(string? input)
    {
        EmailNormalizer.Normalize(input).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Overlong_Email_Fails()
    {
        var local = new string('a', 64);
        var email = $"{local}@{new string('b', 200)}.com";
        EmailNormalizer.Normalize(email).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Control_Characters_Fail()
    {
        EmailNormalizer.Normalize("user\u0007@example.com").IsFailure.ShouldBeTrue();
    }
}

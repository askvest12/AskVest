using AskVest.Modules.Identity.Crypto;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public sealed class SecureRandomTests
{
    private readonly SecureRandom _random = new();

    [Fact]
    public void Token_Is_Unique_And_Base64Url()
    {
        var first = _random.NewToken();
        var second = _random.NewToken();

        first.ShouldNotBe(second);
        first.ShouldMatch("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public void Token_Length_Reflects_Requested_Bytes()
    {
        // 32 bytes -> 43 base64url chars (no padding).
        _random.NewToken(32).Length.ShouldBe(43);
        _random.NewToken(16).Length.ShouldBe(22);
    }

    [Fact]
    public void OneTimeCode_Is_Six_Digits_With_Leading_Zeros()
    {
        var seenLeadingZero = false;
        for (var i = 0; i < 2000; i++)
        {
            var code = _random.NewOneTimeCode();
            code.Length.ShouldBe(6);
            code.ShouldMatch("^[0-9]{6}$");
            seenLeadingZero |= code.StartsWith('0');
        }

        seenLeadingZero.ShouldBeTrue("six-digit codes must be able to start with 0");
    }

    [Fact]
    public void OneTimeCode_Distribution_Is_Roughly_Uniform()
    {
        var counts = new int[10];
        const int samples = 20_000;
        for (var i = 0; i < samples; i++)
        {
            var code = int.Parse(_random.NewOneTimeCode(), System.Globalization.CultureInfo.InvariantCulture);
            counts[code % 10]++;
        }

        // Chi-square-ish sanity check: each bucket ~2000 ± 10%.
        foreach (var count in counts)
        {
            count.ShouldBeGreaterThan(1600);
            count.ShouldBeLessThan(2400);
        }
    }
}

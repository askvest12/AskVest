using AskVest.SharedKernel;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests;

public sealed class CursorCodecTests
{
    [Theory]
    [InlineData("simple")]
    [InlineData("cursor-with-dashes_and_underscores.dots:colons")]
    [InlineData("unicode-é-字符")]
    [InlineData("")]
    public void RoundTrip_PreservesPayload(string payload)
    {
        if (payload.Length == 0)
        {
            // Encode rejects empty payloads by design.
            Should.Throw<ArgumentException>(() => CursorCodec.Encode(payload));
            return;
        }

        var encoded = CursorCodec.Encode(payload);
        CursorCodec.TryDecode(encoded, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(payload);
    }

    [Theory]
    [InlineData("!!!not-base64!!!")]
    [InlineData("a")]
    [InlineData("ab=c")]
    [InlineData("has space inside")]
    public void TryDecode_InvalidBase64Url_ReturnsFalse(string encoded)
    {
        CursorCodec.TryDecode(encoded, out var payload).ShouldBeFalse();
        payload.ShouldBeNull();
    }

    [Fact]
    public void TryDecode_NullOrEmpty_ReturnsFalse()
    {
        CursorCodec.TryDecode(null, out _).ShouldBeFalse();
        CursorCodec.TryDecode("", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryDecode_Oversize_ReturnsFalse()
    {
        var encoded = new string('A', 300);
        CursorCodec.TryDecode(encoded, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryDecode_PaddedInput_Handled()
    {
        // "aGk=" is padded base64 for "hi"; base64url decoding accepts the padding.
        CursorCodec.TryDecode("aGk=", out var payload).ShouldBeTrue();
        payload.ShouldBe("hi");
    }
}

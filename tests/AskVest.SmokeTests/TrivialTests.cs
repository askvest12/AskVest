using Shouldly;
using Xunit;

namespace AskVest.SmokeTests;

public sealed class TrivialTests
{
    [Fact]
    public void TrivialTest()
    {
        true.ShouldBeTrue();
    }
}

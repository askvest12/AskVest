using Shouldly;
using Xunit;

namespace AskVest.UnitTests;

public sealed class TrivialTests
{
    [Fact]
    public void TrivialTest()
    {
        true.ShouldBeTrue();
    }
}

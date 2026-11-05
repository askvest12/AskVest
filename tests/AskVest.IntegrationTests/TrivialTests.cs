using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests;

public sealed class TrivialTests
{
    [Fact]
    public void TrivialTest()
    {
        true.ShouldBeTrue();
    }
}

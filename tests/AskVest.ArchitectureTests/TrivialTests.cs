using Shouldly;
using Xunit;

namespace AskVest.ArchitectureTests;

public sealed class TrivialTests
{
    [Fact]
    public void TrivialTest()
    {
        true.ShouldBeTrue();
    }
}

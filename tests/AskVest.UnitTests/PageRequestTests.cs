using AskVest.SharedKernel;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests;

public sealed class PageRequestTests
{
    [Fact]
    public void Default_HasDefaultLimitAndNoCursor()
    {
        var request = new PageRequest();
        request.Limit.ShouldBe(20);
        request.Cursor.ShouldBeNull();
    }

    [Fact]
    public void MinimumLimit_IsAccepted()
    {
        new PageRequest(1).Limit.ShouldBe(1);
    }

    [Fact]
    public void MaximumLimit_IsAccepted()
    {
        new PageRequest(100).Limit.ShouldBe(100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ZeroAndNegativeLimits_AreRejected(int limit)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new PageRequest(limit));
    }

    [Fact]
    public void LimitAboveMaximum_IsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new PageRequest(101));
    }

    [Fact]
    public void Cursor_IsCarried()
    {
        new PageRequest(20, "abc").Cursor.ShouldBe("abc");
    }
}

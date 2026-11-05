using AskVest.Modules.Identity.Crypto;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public sealed class FailOpenBreachPasswordCheckerTests
{
    private sealed class ThrowingChecker : IBreachPasswordChecker
    {
        public Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("network down");
    }

    private sealed class StubChecker(bool result) : IBreachPasswordChecker
    {
        public Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    [Fact]
    public async Task Failure_Fails_Open()
    {
        var checker = new FailOpenBreachPasswordChecker(new ThrowingChecker(), NullLogger<FailOpenBreachPasswordChecker>.Instance);

        (await checker.IsBreachedAsync("anything", TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Success_Passes_Through()
    {
        var checker = new FailOpenBreachPasswordChecker(new StubChecker(true), NullLogger<FailOpenBreachPasswordChecker>.Instance);

        (await checker.IsBreachedAsync("anything", TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    private sealed class CancelingChecker : IBreachPasswordChecker
    {
        public Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
            => Task.FromCanceled<bool>(cancellationToken);
    }

    [Fact]
    public async Task Cancellation_Is_Not_Swallowed()
    {
        var checker = new FailOpenBreachPasswordChecker(new CancelingChecker(), NullLogger<FailOpenBreachPasswordChecker>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => checker.IsBreachedAsync("x", cts.Token));
    }
}

using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public sealed class CredentialHashingLimiterTests
{
    [Fact]
    public async Task Acquire_And_Release_Allows_Reuse()
    {
        var limiter = new CredentialHashingLimiter(Options.Create(new IdentityOptions
        {
            CredentialHashMaxConcurrency = 1,
            CredentialHashQueueTimeoutSeconds = 5,
        }));

        using (await limiter.AcquireAsync(TestContext.Current.CancellationToken))
        {
            // Held.
        }

        using (await limiter.AcquireAsync(TestContext.Current.CancellationToken))
        {
            // Acquired again after release.
        }
    }

    [Fact]
    public async Task Concurrency_Limit_Is_Enforced()
    {
        var limiter = new CredentialHashingLimiter(Options.Create(new IdentityOptions
        {
            CredentialHashMaxConcurrency = 2,
            CredentialHashQueueTimeoutSeconds = 5,
        }));

        using var first = await limiter.AcquireAsync(TestContext.Current.CancellationToken);
        using var second = await limiter.AcquireAsync(TestContext.Current.CancellationToken);

        var thirdTask = limiter.AcquireAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        thirdTask.IsCompleted.ShouldBeFalse("third acquire must wait while both slots are held");

        first.Dispose();
        using var third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Queue_Timeout_Throws_HashingBusy()
    {
        var limiter = new CredentialHashingLimiter(Options.Create(new IdentityOptions
        {
            CredentialHashMaxConcurrency = 1,
            CredentialHashQueueTimeoutSeconds = 1,
        }));

        using var held = await limiter.AcquireAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<HashingBusyException>(() => limiter.AcquireAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Caller_Cancellation_Propagates_OperationCanceled()
    {
        var limiter = new CredentialHashingLimiter(Options.Create(new IdentityOptions
        {
            CredentialHashMaxConcurrency = 1,
            CredentialHashQueueTimeoutSeconds = 30,
        }));

        using var held = await limiter.AcquireAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(100);

        await Should.ThrowAsync<OperationCanceledException>(() => limiter.AcquireAsync(cts.Token));
    }
}

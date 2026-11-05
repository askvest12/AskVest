using AskVest.Modules.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Crypto;

public sealed class CredentialHashingLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _queueTimeout;

    public CredentialHashingLimiter(IOptions<IdentityOptions> options)
    {
        var identity = options.Value;
        _semaphore = new SemaphoreSlim(identity.CredentialHashMaxConcurrency);
        _queueTimeout = TimeSpan.FromSeconds(identity.CredentialHashQueueTimeoutSeconds);
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_queueTimeout);
        try
        {
            await _semaphore.WaitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HashingBusyException();
        }

        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}

using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using Microsoft.Extensions.Options;

namespace AskVest.IntegrationTests.Identity;

internal sealed class CountingPasswordHasher(IPasswordHasher inner) : IPasswordHasher
{
    private int _verifyCalls;
    private int _hashCalls;

    public int VerifyCalls => _verifyCalls;
    public int HashCalls => _hashCalls;

    public int SnapshotVerifyCalls() => _verifyCalls;

    public Task<string> HashAsync(string password, string pepper, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _hashCalls);
        return inner.HashAsync(password, pepper, cancellationToken);
    }

    public Task<bool> VerifyAsync(string password, string pepper, string storedHash, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _verifyCalls);
        return inner.VerifyAsync(password, pepper, storedHash, cancellationToken);
    }

    public bool NeedsRehash(string storedHash) => inner.NeedsRehash(storedHash);
}

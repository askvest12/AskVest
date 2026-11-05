namespace AskVest.Modules.Identity.Crypto;

public interface IPasswordHasher
{
    Task<string> HashAsync(string password, string pepper, CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(string password, string pepper, string storedHash, CancellationToken cancellationToken = default);

    bool NeedsRehash(string storedHash);
}

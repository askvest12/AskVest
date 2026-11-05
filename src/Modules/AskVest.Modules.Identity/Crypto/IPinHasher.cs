namespace AskVest.Modules.Identity.Crypto;

public interface IPinHasher
{
    Task<string> HashAsync(string pin, string pepper, CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(string pin, string pepper, string storedHash, CancellationToken cancellationToken = default);
}

using System.Security.Cryptography;
using System.Text;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Crypto;

public interface IOneTimeCodeHasher
{
    string Hash(Guid userId, OneTimeCodePurpose purpose, string code);

    bool Verify(Guid userId, OneTimeCodePurpose purpose, string code, string storedHash);
}

public sealed class OneTimeCodeHasher(IOptions<IdentitySecretsOptions> secrets) : IOneTimeCodeHasher
{
    public string Hash(Guid userId, OneTimeCodePurpose purpose, string code)
    {
        var key = Encoding.UTF8.GetBytes(secrets.Value.OneTimeCodeHashSecret!);
        var input = Encoding.UTF8.GetBytes($"{purpose}:{userId:N}:{code}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, input));
    }

    public bool Verify(Guid userId, OneTimeCodePurpose purpose, string code, string storedHash)
    {
        var candidate = Hash(userId, purpose, code);
        var candidateBytes = Encoding.ASCII.GetBytes(candidate);
        var storedBytes = Encoding.ASCII.GetBytes(storedHash);
        return candidateBytes.Length == storedBytes.Length
            && CryptographicOperations.FixedTimeEquals(candidateBytes, storedBytes);
    }
}

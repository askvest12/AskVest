using System.Security.Cryptography;
using System.Text;
using AskVest.Modules.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Crypto;

public interface IKeyedHasher
{
    string Hash(string domain, string value);

    string Sha256Hex(string value);
}

public sealed class KeyedHasher(IOptions<IdentitySecretsOptions> secrets) : IKeyedHasher
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(secrets.Value.OneTimeCodeHashSecret!);

    public string Hash(string domain, string value)
    {
        var input = Encoding.UTF8.GetBytes($"{domain}|{value}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, input));
    }

    public string Sha256Hex(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

using System.Security.Cryptography;
using System.Text;
using AskVest.Modules.Identity.Configuration;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Crypto;

public sealed class Argon2idCore(IOptions<IdentityOptions> options)
{
    private readonly IdentityOptions _options = options.Value;

    public const string AlgorithmId = "argon2id";

    public byte[] DeriveKey(ReadOnlySpan<byte> passwordMaterial, ReadOnlySpan<byte> salt)
    {
        using var argon2 = new Argon2id(passwordMaterial.ToArray())
        {
            Salt = salt.ToArray(),
            DegreeOfParallelism = _options.Argon2Parallelism,
            MemorySize = _options.Argon2MemoryKiB,
            Iterations = _options.Argon2Iterations,
        };
        return argon2.GetBytes(_options.CredentialHashLengthBytes);
    }

    public byte[] NewSalt()
    {
        var salt = new byte[_options.CredentialSaltLengthBytes];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    public static byte[] PepperedMaterial(string secret, string pepper, string domain)
    {
        var input = Encoding.UTF8.GetBytes($"{domain}:{secret}");
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(pepper), input);
    }
}

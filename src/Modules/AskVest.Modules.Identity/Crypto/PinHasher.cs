using System.Security.Cryptography;
using System.Text;
using AskVest.Modules.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Crypto;

public sealed class PinHasher(
    Argon2idCore core,
    CredentialHashingLimiter limiter,
    IOptions<IdentityOptions> options) : IPinHasher
{
    private const string Domain = "pin";
    private readonly IdentityOptions _options = options.Value;

    public async Task<string> HashAsync(string pin, string pepper, CancellationToken cancellationToken = default)
    {
        using var permit = await limiter.AcquireAsync(cancellationToken);
        var material = Argon2idCore.PepperedMaterial(pin, pepper, Domain);
        var salt = core.NewSalt();
        var hash = core.DeriveKey(material, salt);
        return string.Join('$',
            Argon2idCore.AlgorithmId,
            $"v=1",
            $"m={_options.Argon2MemoryKiB},t={_options.Argon2Iterations},p={_options.Argon2Parallelism}",
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public async Task<bool> VerifyAsync(string pin, string pepper, string storedHash, CancellationToken cancellationToken = default)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 5 || parts[0] != "argon2id" || parts[1] != "v=1")
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        using var permit = await limiter.AcquireAsync(cancellationToken);
        var material = Argon2idCore.PepperedMaterial(pin, pepper, Domain);
        var candidate = core.DeriveKey(material, salt);
        return CryptographicOperations.FixedTimeEquals(candidate, expected);
    }
}

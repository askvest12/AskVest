using System.Security.Cryptography;
using System.Text;
using AskVest.Modules.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Crypto;

public sealed class PasswordHasher(
    Argon2idCore core,
    CredentialHashingLimiter limiter,
    IOptions<IdentityOptions> options) : IPasswordHasher
{
    private const string Domain = "password";
    private readonly IdentityOptions _options = options.Value;

    public async Task<string> HashAsync(string password, string pepper, CancellationToken cancellationToken = default)
    {
        using var permit = await limiter.AcquireAsync(cancellationToken);
        var material = Argon2idCore.PepperedMaterial(password, pepper, Domain);
        var salt = core.NewSalt();
        var hash = core.DeriveKey(material, salt);
        return string.Join('$',
            Argon2idCore.AlgorithmId,
            $"v=1",
            $"m={_options.Argon2MemoryKiB},t={_options.Argon2Iterations},p={_options.Argon2Parallelism}",
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public async Task<bool> VerifyAsync(string password, string pepper, string storedHash, CancellationToken cancellationToken = default)
    {
        var parsed = Parse(storedHash);
        if (parsed is null)
        {
            return false;
        }

        using var permit = await limiter.AcquireAsync(cancellationToken);
        var material = Argon2idCore.PepperedMaterial(password, pepper, Domain);
        var candidate = core.DeriveKey(material, parsed.Value.Salt);
        return CryptographicOperations.FixedTimeEquals(candidate, parsed.Value.Hash);
    }

    public bool NeedsRehash(string storedHash)
    {
        var parsed = Parse(storedHash);
        return parsed is null
            || parsed.Value.MemoryKiB != _options.Argon2MemoryKiB
            || parsed.Value.Iterations != _options.Argon2Iterations
            || parsed.Value.Parallelism != _options.Argon2Parallelism
            || parsed.Value.Hash.Length != _options.CredentialHashLengthBytes;
    }

    private static (byte[] Salt, byte[] Hash, int MemoryKiB, int Iterations, int Parallelism)? Parse(string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 5 || parts[0] != "argon2id" || parts[1] != "v=1")
        {
            return null;
        }

        var parameters = parts[2].Split(',');
        if (parameters.Length != 3
            || !parameters[0].StartsWith("m=", StringComparison.Ordinal)
            || !parameters[1].StartsWith("t=", StringComparison.Ordinal)
            || !parameters[2].StartsWith("p=", StringComparison.Ordinal)
            || !int.TryParse(parameters[0][2..], out var memory)
            || !int.TryParse(parameters[1][2..], out var iterations)
            || !int.TryParse(parameters[2][2..], out var parallelism))
        {
            return null;
        }

        try
        {
            return (Convert.FromBase64String(parts[3]), Convert.FromBase64String(parts[4]), memory, iterations, parallelism);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

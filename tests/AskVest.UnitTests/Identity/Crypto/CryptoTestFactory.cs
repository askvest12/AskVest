using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public static class CryptoTestFactory
{
    public static IdentityOptions FastOptions() => new()
    {
        Argon2MemoryKiB = 1024,
        Argon2Iterations = 1,
        Argon2Parallelism = 1,
        CredentialHashLengthBytes = 32,
        CredentialSaltLengthBytes = 16,
        CredentialHashMaxConcurrency = 4,
        CredentialHashQueueTimeoutSeconds = 1,
        AccessTokenIssuer = "askvest-test",
        AccessTokenAudience = "askvest-test-api",
        PasswordMinLength = 10,
        PasswordMaxLength = 128,
        PasswordBreachCheckEnabled = false,
    };

    public static Argon2idCore CreateCore(IdentityOptions? options = null)
        => new(Options.Create(options ?? FastOptions()));

    public static CredentialHashingLimiter CreateLimiter(IdentityOptions? options = null)
        => new(Options.Create(options ?? FastOptions()));

    public static PasswordHasher CreatePasswordHasher(IdentityOptions? options = null, CredentialHashingLimiter? limiter = null)
    {
        var opts = options ?? FastOptions();
        limiter ??= CreateLimiter(opts);
        return new PasswordHasher(CreateCore(opts), limiter, Options.Create(opts));
    }

    public static PinHasher CreatePinHasher(IdentityOptions? options = null, CredentialHashingLimiter? limiter = null)
    {
        var opts = options ?? FastOptions();
        limiter ??= CreateLimiter(opts);
        return new PinHasher(CreateCore(opts), limiter, Options.Create(opts));
    }

    public static IdentitySecretsOptions Secrets(string? previousKey = null) => new()
    {
        AccessTokenSigningKey = GenerateEcKey(),
        AccessTokenSigningKeyPrevious = previousKey,
        PasswordPepper = new string('p', 40),
        OneTimeCodeHashSecret = new string('o', 40),
        PasswordPepperVersion = 1,
    };

    public static string GenerateEcKey()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportPkcs8PrivateKey());
    }
}

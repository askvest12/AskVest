using System.Security.Cryptography;
using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public sealed class AccessTokenServiceTests
{
    private static AccessTokenService CreateService(
        IdentityOptions? options = null,
        IdentitySecretsOptions? secrets = null,
        TimeProvider? timeProvider = null)
        => new(
            Options.Create(options ?? CryptoTestFactory.FastOptions()),
            Options.Create(secrets ?? CryptoTestFactory.Secrets()),
            timeProvider ?? TimeProvider.System);

    [Fact]
    public async Task Issued_Token_Validates()
    {
        using var service = CreateService();
        var userId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        var issued = service.Issue(userId, sessionId);

        var validation = await service.ValidateAsync(issued.Token);

        validation.IsValid.ShouldBeTrue();
        validation.IsExpired.ShouldBeFalse();
        validation.Principal.ShouldNotBeNull();
        validation.Principal!.FindFirst("sub")!.Value.ShouldBe(userId.ToString("D"));
        validation.Principal.FindFirst("sid")!.Value.ShouldBe(sessionId.ToString("D"));
    }

    [Fact]
    public async Task Expired_Token_Reports_Expired()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(TimeProvider.System.GetUtcNow());
        using var service = CreateService(timeProvider: time);
        var issued = service.Issue(Guid.CreateVersion7(), Guid.CreateVersion7());

        time.Advance(TimeSpan.FromMinutes(11));
        var validation = await service.ValidateAsync(issued.Token);

        validation.IsValid.ShouldBeFalse();
        validation.IsExpired.ShouldBeTrue();
    }

    [Fact]
    public async Task Tampered_Token_Is_Invalid()
    {
        using var service = CreateService();
        var issued = service.Issue(Guid.CreateVersion7(), Guid.CreateVersion7());
        var tampered = issued.Token[..^2] + (issued.Token[^2] == 'a' ? "bb" : "aa");

        var validation = await service.ValidateAsync(tampered);

        validation.IsValid.ShouldBeFalse();
        validation.IsExpired.ShouldBeFalse();
    }

    [Fact]
    public async Task Garbage_Token_Is_Invalid()
    {
        using var service = CreateService();
        (await service.ValidateAsync("not.a.jwt")).IsValid.ShouldBeFalse();
        (await service.ValidateAsync("")).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task None_Algorithm_Token_Is_Rejected()
    {
        using var service = CreateService();
        var header = Base64UrlEncode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { alg = "none", typ = "JWT" }));
        var payload = Base64UrlEncode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "askvest-test",
            aud = "askvest-test-api",
            sub = Guid.CreateVersion7().ToString(),
            exp = TimeProvider.System.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds(),
        }));
        var unsigned = $"{header}.{payload}.";

        (await service.ValidateAsync(unsigned)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Symmetric_Algorithm_Confusion_Is_Rejected()
    {
        using var service = CreateService();
        // HS256 token signed with a symmetric key must not validate against EC keys.
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var header = Base64UrlEncode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64UrlEncode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "askvest-test",
            aud = "askvest-test-api",
            sub = Guid.CreateVersion7().ToString(),
            exp = TimeProvider.System.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds(),
        }));
        var signingInput = $"{header}.{payload}";
        var signature = Base64UrlEncode(HMACSHA256.HashData(keyBytes, System.Text.Encoding.ASCII.GetBytes(signingInput)));

        (await service.ValidateAsync($"{signingInput}.{signature}")).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Wrong_Issuer_Is_Rejected()
    {
        using var service = CreateService();
        var issued = service.Issue(Guid.CreateVersion7(), Guid.CreateVersion7());
        // Re-issue under different issuer options.
        using var other = CreateService(options: new IdentityOptions
        {
            AccessTokenIssuer = "evil-issuer",
            AccessTokenAudience = "askvest-test-api",
            AccessTokenLifetime = TimeSpan.FromMinutes(10),
            Argon2MemoryKiB = 1024,
            Argon2Iterations = 1,
            Argon2Parallelism = 1,
            CredentialHashLengthBytes = 32,
            CredentialSaltLengthBytes = 16,
            CredentialHashMaxConcurrency = 4,
            CredentialHashQueueTimeoutSeconds = 1,
            PasswordMinLength = 10,
            PasswordMaxLength = 128,
            PasswordBreachCheckEnabled = false,
        });
        var foreign = other.Issue(Guid.CreateVersion7(), Guid.CreateVersion7());

        (await service.ValidateAsync(foreign.Token)).IsValid.ShouldBeFalse();
        (await service.ValidateAsync(issued.Token)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Retired_Previous_Key_Still_Validates()
    {
        var previousKey = CryptoTestFactory.GenerateEcKey();
        var secrets = CryptoTestFactory.Secrets(previousKey: previousKey);
        using var service = CreateService(secrets: secrets);

        // Token signed with the previous key validates; one with an unknown key does not.
        using var previousOnly = CreateService(secrets: new IdentitySecretsOptions
        {
            AccessTokenSigningKey = previousKey,
            PasswordPepper = secrets.PasswordPepper,
            OneTimeCodeHashSecret = secrets.OneTimeCodeHashSecret,
        });
        var legacyToken = previousOnly.Issue(Guid.CreateVersion7(), Guid.CreateVersion7());

        (await service.ValidateAsync(legacyToken.Token)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_Key_Id_Is_Rejected()
    {
        using var service = CreateService();
        using var stranger = CreateService();
        var foreignToken = stranger.Issue(Guid.CreateVersion7(), Guid.CreateVersion7());

        (await service.ValidateAsync(foreignToken.Token)).IsValid.ShouldBeFalse();
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

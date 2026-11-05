using AskVest.Modules.Identity.Crypto;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public sealed class PinHasherTests
{
    private const string Pepper = "test-pepper-value-0123456789abcdef";

    [Fact]
    public async Task Hash_Verify_Roundtrip()
    {
        var hasher = CryptoTestFactory.CreatePinHasher();
        var hash = await hasher.HashAsync("123456", Pepper, TestContext.Current.CancellationToken);

        (await hasher.VerifyAsync("123456", Pepper, hash, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task Wrong_Pin_Fails()
    {
        var hasher = CryptoTestFactory.CreatePinHasher();
        var hash = await hasher.HashAsync("123456", Pepper, TestContext.Current.CancellationToken);

        (await hasher.VerifyAsync("654321", Pepper, hash, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Different_Pepper_Fails()
    {
        var hasher = CryptoTestFactory.CreatePinHasher();
        var hash = await hasher.HashAsync("123456", Pepper, TestContext.Current.CancellationToken);

        (await hasher.VerifyAsync("123456", "different-pepper-0123456789", hash, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Password_And_Pin_Domains_Do_Not_Collide()
    {
        // Same material hashed under different domains must differ.
        var passwordHasher = CryptoTestFactory.CreatePasswordHasher();
        var pinHasher = CryptoTestFactory.CreatePinHasher();
        var passwordHash = await passwordHasher.HashAsync("123456", Pepper, TestContext.Current.CancellationToken);
        var pinHash = await pinHasher.HashAsync("123456", Pepper, TestContext.Current.CancellationToken);

        (await pinHasher.VerifyAsync("123456", Pepper, passwordHash, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await passwordHasher.VerifyAsync("123456", Pepper, pinHash, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Malformed_Hash_Returns_False()
    {
        var hasher = CryptoTestFactory.CreatePinHasher();
        (await hasher.VerifyAsync("123456", Pepper, "junk", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await hasher.VerifyAsync("123456", Pepper, "argon2id$v=1$m=1024,t=1,p=1$notbase64$notbase64", TestContext.Current.CancellationToken)).ShouldBeFalse();
    }
}

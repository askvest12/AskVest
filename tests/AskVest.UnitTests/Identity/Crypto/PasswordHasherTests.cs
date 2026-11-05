using AskVest.Modules.Identity.Crypto;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public sealed class PasswordHasherTests
{
    private const string Pepper = "test-pepper-value-0123456789abcdef";

    [Fact]
    public async Task Hash_Verify_Roundtrip()
    {
        var hasher = CryptoTestFactory.CreatePasswordHasher();
        var hash = await hasher.HashAsync("correct horse battery", Pepper, TestContext.Current.CancellationToken);

        (await hasher.VerifyAsync("correct horse battery", Pepper, hash, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task Wrong_Password_Fails()
    {
        var hasher = CryptoTestFactory.CreatePasswordHasher();
        var hash = await hasher.HashAsync("correct horse battery", Pepper, TestContext.Current.CancellationToken);

        (await hasher.VerifyAsync("wrong password here!", Pepper, hash, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Different_Pepper_Fails()
    {
        var hasher = CryptoTestFactory.CreatePasswordHasher();
        var hash = await hasher.HashAsync("correct horse battery", Pepper, TestContext.Current.CancellationToken);

        (await hasher.VerifyAsync("correct horse battery", "other-pepper-0123456789abcdef", hash, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Hashes_Are_Unique_Per_Salt()
    {
        var hasher = CryptoTestFactory.CreatePasswordHasher();
        var first = await hasher.HashAsync("same password value", Pepper, TestContext.Current.CancellationToken);
        var second = await hasher.HashAsync("same password value", Pepper, TestContext.Current.CancellationToken);

        first.ShouldNotBe(second);
        first.ShouldStartWith("argon2id$v=1$m=1024,t=1,p=1$");
    }

    [Fact]
    public async Task Malformed_Hash_Returns_False()
    {
        var hasher = CryptoTestFactory.CreatePasswordHasher();
        (await hasher.VerifyAsync("anything", Pepper, "not-a-hash", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await hasher.VerifyAsync("anything", Pepper, "argon2id$v=1$m=1024,t=1,p=1$!!!$!!!", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await hasher.VerifyAsync("anything", Pepper, "bcrypt$v=1$m=1024,t=1,p=1$AAAA$AAAA", TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public void NeedsRehash_Detects_Old_Parameters()
    {
        var hasher = CryptoTestFactory.CreatePasswordHasher();
        var current = "argon2id$v=1$m=1024,t=1,p=1$" +
            Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]);
        var oldParams = "argon2id$v=1$m=4096,t=3,p=4$" +
            Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]);
        var shortHash = "argon2id$v=1$m=1024,t=1,p=1$" +
            Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[16]);

        hasher.NeedsRehash(current).ShouldBeFalse();
        hasher.NeedsRehash(oldParams).ShouldBeTrue();
        hasher.NeedsRehash(shortHash).ShouldBeTrue();
        hasher.NeedsRehash("garbage").ShouldBeTrue();
    }
}

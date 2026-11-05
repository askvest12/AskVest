using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Domain.Enums;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public sealed class OneTimeCodeHasherTests
{
    private static OneTimeCodeHasher CreateHasher(string? secret = null)
        => new OneTimeCodeHasher(Options.Create(new IdentitySecretsOptions
        {
            OneTimeCodeHashSecret = secret ?? new string('s', 40),
        }));

    [Fact]
    public void Hash_Verify_Roundtrip()
    {
        var hasher = CreateHasher();
        var userId = Guid.CreateVersion7();
        var hash = hasher.Hash(userId, OneTimeCodePurpose.EmailVerification, "123456");

        hasher.Verify(userId, OneTimeCodePurpose.EmailVerification, "123456", hash).ShouldBeTrue();
    }

    [Fact]
    public void Wrong_Code_Fails()
    {
        var hasher = CreateHasher();
        var userId = Guid.CreateVersion7();
        var hash = hasher.Hash(userId, OneTimeCodePurpose.EmailVerification, "123456");

        hasher.Verify(userId, OneTimeCodePurpose.EmailVerification, "654321", hash).ShouldBeFalse();
    }

    [Fact]
    public void Different_User_Fails()
    {
        var hasher = CreateHasher();
        var hash = hasher.Hash(Guid.CreateVersion7(), OneTimeCodePurpose.EmailVerification, "123456");

        hasher.Verify(Guid.CreateVersion7(), OneTimeCodePurpose.EmailVerification, "123456", hash).ShouldBeFalse();
    }

    [Fact]
    public void Different_Purpose_Fails()
    {
        var hasher = CreateHasher();
        var userId = Guid.CreateVersion7();
        var hash = hasher.Hash(userId, OneTimeCodePurpose.EmailVerification, "123456");

        hasher.Verify(userId, OneTimeCodePurpose.PasswordReset, "123456", hash).ShouldBeFalse();
    }

    [Fact]
    public void Different_Secret_Produces_Different_Hash()
    {
        var userId = Guid.CreateVersion7();
        var first = CreateHasher(new string('a', 40)).Hash(userId, OneTimeCodePurpose.EmailVerification, "123456");
        var second = CreateHasher(new string('b', 40)).Hash(userId, OneTimeCodePurpose.EmailVerification, "123456");

        first.ShouldNotBe(second);
    }

    [Fact]
    public void Malformed_Stored_Hash_Fails()
    {
        var hasher = CreateHasher();
        hasher.Verify(Guid.CreateVersion7(), OneTimeCodePurpose.EmailVerification, "123456", "short").ShouldBeFalse();
        hasher.Verify(Guid.CreateVersion7(), OneTimeCodePurpose.EmailVerification, "123456", new string('0', 64)).ShouldBeFalse();
    }
}

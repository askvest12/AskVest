using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Crypto;

public sealed class PasswordPolicyTests
{
    private sealed class StubBreachChecker(bool breached) : IBreachPasswordChecker
    {
        public Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
            => Task.FromResult(breached);
    }

    private static PasswordPolicy CreatePolicy(bool breachCheckEnabled = false, bool breached = false)
    {
        var options = CryptoTestFactory.FastOptions();
        options.PasswordBreachCheckEnabled = breachCheckEnabled;
        return new PasswordPolicy(
            Options.Create(options),
            new StubBreachChecker(breached));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short1")]
    [InlineData("onlylettersnospacesok")]
    [InlineData("12345678901234")]
    [InlineData("password123")]
    [InlineData("Password123")]
    [InlineData("askvest123")]
    public async Task Weak_Passwords_Fail(string? password)
    {
        var policy = CreatePolicy();
        (await policy.ValidateAsync(password, "user@example.com", TestContext.Current.CancellationToken)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Strong_Password_Passes()
    {
        var policy = CreatePolicy();
        (await policy.ValidateAsync("Tr0ub4dor&secure", "user@example.com", TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Password_Containing_Email_Local_Part_Fails()
    {
        var policy = CreatePolicy();
        (await policy.ValidateAsync("myusername99!", "myusername@example.com", TestContext.Current.CancellationToken)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Breached_Password_Fails_When_Check_Enabled()
    {
        var policy = CreatePolicy(breachCheckEnabled: true, breached: true);
        (await policy.ValidateAsync("Str0ng!Password", "user@example.com", TestContext.Current.CancellationToken)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Breach_Check_Skipped_When_Disabled()
    {
        var policy = CreatePolicy(breachCheckEnabled: false, breached: true);
        (await policy.ValidateAsync("Str0ng!Password", "user@example.com", TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Too_Long_Password_Fails()
    {
        var policy = CreatePolicy();
        var password = new string('a', 129) + "1";
        (await policy.ValidateAsync(password, null, TestContext.Current.CancellationToken)).IsFailure.ShouldBeTrue();
    }
}

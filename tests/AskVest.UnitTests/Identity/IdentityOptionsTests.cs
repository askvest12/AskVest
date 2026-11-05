using AskVest.Modules.Identity;
using AskVest.Modules.Identity.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity;

public sealed class IdentityOptionsTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityModule(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> ValidSecrets() => new()
    {
        ["Identity:Secrets:AccessTokenSigningKey"] = new string('a', 48),
        ["Identity:Secrets:PasswordPepper"] = new string('b', 48),
        ["Identity:Secrets:OneTimeCodeHashSecret"] = new string('c', 48),
        ["Identity:Secrets:PasswordPepperVersion"] = "1",
        ["ConnectionStrings:Platform"] = "Host=localhost;Database=none",
    };

    [Fact]
    public void Valid_Secrets_Pass_Validation()
    {
        using var provider = BuildProvider(ValidSecrets());
        var options = provider.GetRequiredService<IOptions<IdentitySecretsOptions>>().Value;
        options.PasswordPepperVersion.ShouldBe(1);
    }

    [Fact]
    public void Missing_Secrets_Fail_Startup_Validation()
    {
        var settings = ValidSecrets();
        settings.Remove("Identity:Secrets:AccessTokenSigningKey");
        settings.Remove("Identity:Secrets:PasswordPepper");
        settings.Remove("Identity:Secrets:OneTimeCodeHashSecret");
        using var provider = BuildProvider(settings);
        Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<IdentitySecretsOptions>>().Value);
    }

    [Fact]
    public void Short_Secrets_Fail_Validation()
    {
        var settings = ValidSecrets();
        settings["Identity:Secrets:PasswordPepper"] = "short";
        using var provider = BuildProvider(settings);
        Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<IdentitySecretsOptions>>().Value);
    }

    [Fact]
    public void Invalid_Limits_Fail_Validation()
    {
        var settings = ValidSecrets();
        settings["Identity:AccessTokenLifetime"] = "00:00:00";
        using var provider = BuildProvider(settings);
        Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<IdentityOptions>>().Value);
    }

    [Fact]
    public void Spec_Defaults_Are_Present()
    {
        var options = new IdentityOptions();
        options.AccessTokenLifetime.ShouldBe(TimeSpan.FromMinutes(10));
        options.RefreshIdleLifetime.ShouldBe(TimeSpan.FromDays(30));
        options.RefreshAbsoluteLifetime.ShouldBe(TimeSpan.FromDays(90));
        options.RefreshReuseGrace.ShouldBe(TimeSpan.FromSeconds(10));
        options.OneTimeCodeLifetime.ShouldBe(TimeSpan.FromMinutes(10));
        options.OneTimeCodeMaxAttempts.ShouldBe(5);
        options.ResendCooldown.ShouldBe(TimeSpan.FromSeconds(60));
        options.CodesPerDestinationPerHour.ShouldBe(5);
        options.LoginFailuresPerAccount.ShouldBe(5);
        options.LoginFailuresPerClientIp.ShouldBe(20);
        options.LoginThrottleWindow.ShouldBe(TimeSpan.FromMinutes(15));
        options.LoginThrottleDuration.ShouldBe(TimeSpan.FromMinutes(15));
        options.LoginThrottleMaxDuration.ShouldBe(TimeSpan.FromHours(24));
        options.PinLength.ShouldBe(6);
        options.PinMaxFailedAttempts.ShouldBe(5);
        options.PinLockDuration.ShouldBe(TimeSpan.FromMinutes(30));
        options.PinLockMaxDuration.ShouldBe(TimeSpan.FromHours(24));
        options.StepUpGrantLifetime.ShouldBe(TimeSpan.FromSeconds(120));
        options.MaxActiveSessions.ShouldBe(10);
        options.SessionLastUsedInterval.ShouldBe(TimeSpan.FromSeconds(60));
        options.DeletionCoolingOff.ShouldBe(TimeSpan.FromDays(30));
        options.PasswordMinLength.ShouldBe(10);
        options.PasswordMaxLength.ShouldBe(128);
        options.EmailMaxLength.ShouldBe(254);
    }
}

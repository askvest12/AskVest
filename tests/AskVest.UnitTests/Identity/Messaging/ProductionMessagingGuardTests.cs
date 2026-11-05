using AskVest.Modules.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Messaging;

public sealed class ProductionMessagingGuardTests
{
    private static ServiceCollection BuildServices(Dictionary<string, string?> settings, bool production)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var environment = new TestHostEnvironment
        {
            EnvironmentName = production ? Environments.Production : Environments.Development,
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityModule(configuration, environment);
        return services;
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["ConnectionStrings:Platform"] = "Host=localhost;Database=none",
        ["Identity:Secrets:AccessTokenSigningKey"] = new string('a', 48),
        ["Identity:Secrets:PasswordPepper"] = new string('b', 48),
        ["Identity:Secrets:OneTimeCodeHashSecret"] = new string('c', 48),
    };

    [Fact]
    public void Production_Rejects_Development_Sms_Adapter()
    {
        var settings = BaseSettings();
        settings["Messaging:Smtp:Host"] = "smtp.provider.example";
        settings["Messaging:Sms:Mode"] = "Development";

        Should.Throw<InvalidOperationException>(() => BuildServices(settings, production: true));
    }

    [Fact]
    public void Production_Rejects_Mailpit_Smtp_Host()
    {
        var settings = BaseSettings();
        settings["Messaging:Smtp:Host"] = "mailpit";

        Should.Throw<InvalidOperationException>(() => BuildServices(settings, production: true));
    }

    [Fact]
    public void Production_Rejects_Loopback_Smtp_Host()
    {
        var settings = BaseSettings();
        settings["Messaging:Smtp:Host"] = "127.0.0.1";

        Should.Throw<InvalidOperationException>(() => BuildServices(settings, production: true));
    }

    [Fact]
    public void Production_Rejects_Missing_Smtp()
    {
        Should.Throw<InvalidOperationException>(() => BuildServices(BaseSettings(), production: true));
    }

    [Fact]
    public void Production_Accepts_Real_Smtp()
    {
        var settings = BaseSettings();
        settings["Messaging:Smtp:Host"] = "smtp.provider.example";
        settings["Messaging:Smtp:FromAddress"] = "noreply@askvest.example";

        var services = BuildServices(settings, production: true);
        services.ShouldNotBeNull();
    }

    [Fact]
    public void Development_Allows_Mailpit_And_Dev_Sms()
    {
        var settings = BaseSettings();
        settings["Messaging:Smtp:Host"] = "localhost";
        settings["Messaging:Smtp:Port"] = "1025";
        settings["Messaging:Sms:Mode"] = "Development";
        settings["Messaging:Sms:ForwardTo"] = "dev@localhost";

        var services = BuildServices(settings, production: false);
        services.ShouldNotBeNull();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

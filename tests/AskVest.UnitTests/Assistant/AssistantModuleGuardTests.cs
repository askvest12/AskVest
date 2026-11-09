using AskVest.Modules.Assistant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Assistant;

public sealed class AssistantModuleGuardTests
{
    private static IConfiguration ConfigurationWith(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration ValidConfiguration() => ConfigurationWith(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Platform"] = "Host=localhost;Database=x",
    });

    [Fact]
    public void Fake_Model_In_Production_Throws()
    {
        var services = new ServiceCollection();
        Should.Throw<InvalidOperationException>(
            () => services.AddAssistantModule(ValidConfiguration(), new StubEnvironment("Production")));
    }

    [Fact]
    public void Unknown_Provider_Throws_In_Any_Environment()
    {
        var services = new ServiceCollection();
        var configuration = ConfigurationWith(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = "Host=localhost;Database=x",
            ["Assistant:Provider"] = "openai",
        });
        Should.Throw<InvalidOperationException>(
            () => services.AddAssistantModule(configuration, new StubEnvironment("Development")));
    }

    [Fact]
    public void Missing_Connection_String_Throws()
    {
        var services = new ServiceCollection();
        Should.Throw<InvalidOperationException>(
            () => services.AddAssistantModule(new ConfigurationBuilder().Build(), new StubEnvironment("Development")));
    }

    [Fact]
    public void Fake_Model_In_Development_Registers()
    {
        var services = new ServiceCollection();
        services.AddAssistantModule(ValidConfiguration(), new StubEnvironment("Development"));
        services.ShouldContain(descriptor => descriptor.ServiceType.Name.Contains("ILanguageModelClient"));
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

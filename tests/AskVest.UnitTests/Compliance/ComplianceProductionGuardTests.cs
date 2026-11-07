using AskVest.Modules.Compliance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Compliance;

public sealed class ComplianceProductionGuardTests
{
    private static ServiceCollection ServicesWith(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddComplianceModule(configuration, new StubEnvironment("Production"));
        return services;
    }

    private static IConfiguration ConfigurationWith(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Fake_Providers_With_RealMoney_Countries_In_Production_Throws()
    {
        var configuration = ConfigurationWith(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = "Host=localhost;Database=x",
            ["Compliance:Kyc:WebhookSecret"] = "0123456789abcdef0123456789abcdef",
            ["Compliance:RealMoneyCountries:0"] = "NG",
        });

        Should.Throw<InvalidOperationException>(() => ServicesWith(configuration));
    }

    [Fact]
    public void Fake_Providers_With_RealMoney_Countries_In_Development_Do_Not_Throw()
    {
        var services = new ServiceCollection();
        var configuration = ConfigurationWith(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = "Host=localhost;Database=x",
            ["Compliance:Kyc:WebhookSecret"] = "0123456789abcdef0123456789abcdef",
            ["Compliance:RealMoneyCountries:0"] = "NG",
        });

        services.AddComplianceModule(configuration, new StubEnvironment("Development"));
    }

    [Fact]
    public void Fake_Providers_Without_RealMoney_Countries_In_Production_Do_Not_Throw()
    {
        var services = new ServiceCollection();
        var configuration = ConfigurationWith(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = "Host=localhost;Database=x",
            ["Compliance:Kyc:WebhookSecret"] = "0123456789abcdef0123456789abcdef",
        });

        services.AddComplianceModule(configuration, new StubEnvironment("Production"));
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

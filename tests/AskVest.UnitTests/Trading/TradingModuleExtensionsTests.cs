using AskVest.Modules.Trading;
using AskVest.Modules.Trading.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Trading;

public sealed class TradingModuleExtensionsTests
{
    private static ServiceCollection ServicesWith(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTradingModule(configuration, new TestEnvironment("Development"));
        return services;
    }

    private static Dictionary<string, string?> ValidConfig()
        => new()
        {
            ["ConnectionStrings:Platform"] = "Host=localhost;Database=none;Username=none;Password=none",
            ["Trading:Gateway:Routes:fake:SettlementAsset"] = "USD",
        };

    [Fact]
    public void Valid_Configuration_Resolves()
    {
        var provider = ServicesWith(ValidConfig()).BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TradingOptions>>().Value;
        options.BuyEnabled.ShouldBeTrue();
        options.SellEnabled.ShouldBeTrue();
        options.Gateway.Routes["fake"].Provider.ShouldBe("fake");
    }

    [Fact]
    public void Invalid_Configuration_Fails_Validation()
    {
        var values = ValidConfig();
        values["Trading:MinOrderAmount"] = "-1";
        var provider = ServicesWith(values).BuildServiceProvider();
        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<TradingOptions>>().Value);
    }

    [Fact]
    public void Unknown_Gateway_Provider_Fails_Validation()
    {
        var values = ValidConfig();
        values["Trading:Gateway:Routes:fake:Provider"] = "realvenue";
        var provider = ServicesWith(values).BuildServiceProvider();
        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<TradingOptions>>().Value);
    }

    [Fact]
    public void Fake_Gateway_In_Production_Stops_Startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(ValidConfig())
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        Should.Throw<InvalidOperationException>(
            () => services.AddTradingModule(configuration, new TestEnvironment("Production")));
    }

    [Fact]
    public void Missing_Connection_String_Stops_Startup()
    {
        var values = ValidConfig();
        values.Remove("ConnectionStrings:Platform");
        Should.Throw<InvalidOperationException>(() => ServicesWith(values));
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

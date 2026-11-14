using System.Text;
using System.Text.Json;
using AskVest.Modules.Wallets;
using AskVest.Modules.Wallets.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Wallets;

public sealed class WalletsModuleExtensionsTests
{
    private static ServiceCollection ServicesWith(
        Dictionary<string, string?> values,
        string environment = "Development",
        string? custodyProvider = "fake",
        bool includeCustodyRoute = true)
    {
        // Route keys contain ':' which the in-memory provider treats as a
        // hierarchy delimiter, so routes come from JSON instead.
        object routes = includeCustodyRoute
            ? new[] { new { ChainId = "eip155:11155111", Provider = custodyProvider } }
            : Array.Empty<object>();
        var json = JsonSerializer.Serialize(new
        {
            Wallets = new
            {
                Custody = new { Routes = routes },
                Screening = new { Provider = "fake" },
            },
        });
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWalletsModule(configuration, new TestEnvironment(environment));
        return services;
    }

    private static Dictionary<string, string?> ValidConfig()
        => new()
        {
            ["ConnectionStrings:Platform"] = "Host=localhost;Database=none;Username=none;Password=none",
        };

    [Fact]
    public void Valid_Configuration_Resolves()
    {
        var provider = ServicesWith(ValidConfig()).BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WalletsOptions>>().Value;
        options.AddressBookMaximum.ShouldBe(10);
        options.Custody.RouteProviders["eip155:11155111"].ShouldBe("fake");
        options.Screening.Provider.ShouldBe("fake");
    }

    [Fact]
    public void Invalid_Configuration_Fails_Validation()
    {
        var values = ValidConfig();
        values["Wallets:AddressBookMaximum"] = "0";
        var provider = ServicesWith(values).BuildServiceProvider();
        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<WalletsOptions>>().Value);
    }

    [Fact]
    public void Unknown_Custody_Provider_Fails_Validation()
    {
        var provider = ServicesWith(ValidConfig(), custodyProvider: "realvault")
            .BuildServiceProvider();
        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<WalletsOptions>>().Value);
    }

    [Fact]
    public void Bad_Fee_Key_Fails_Validation()
    {
        var values = ValidConfig();
        values["Wallets:WithdrawalFees:not-a-guid"] = "1";
        var provider = ServicesWith(values).BuildServiceProvider();
        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<WalletsOptions>>().Value);
    }

    [Fact]
    public void Fake_Providers_In_Production_Stop_Startup()
    {
        Should.Throw<InvalidOperationException>(
            () => ServicesWith(ValidConfig(), "Production"));
    }

    [Fact]
    public void Fake_Screening_In_Production_Stops_Startup_Even_Without_Custody_Routes()
    {
        Should.Throw<InvalidOperationException>(
            () => ServicesWith(ValidConfig(), "Production", includeCustodyRoute: false));
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

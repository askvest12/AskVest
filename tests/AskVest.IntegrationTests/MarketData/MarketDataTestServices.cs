using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Catalog;
using AskVest.Modules.Compliance;
using AskVest.Modules.Identity;
using AskVest.Modules.Learning;
using AskVest.Modules.Learning.Configuration;
using AskVest.Modules.Ledger;
using AskVest.Modules.MarketData;
using AskVest.Modules.MarketData.Services;
using AskVest.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AskVest.IntegrationTests.MarketData;

internal static class MarketDataTestServices
{
    public static ServiceProvider CreateProvider(
        PostgresFixture fixture,
        TimeProvider clock,
        IReadOnlyDictionary<string, string?>? marketDataSettings = null,
        string environmentName = "Test")
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = fixture.OwnerConnectionString,
            ["Compliance:Kyc:WebhookSecret"] = TestComplianceSecrets.Entries["Compliance:Kyc:WebhookSecret"],
            ["Compliance:RealMoneyCountries:0"] = "US",
            ["Compliance:RealMoneyCountries:1"] = "NG",
            ["MarketData:Provider"] = "fake",
            ["MarketData:PollIntervalSeconds"] = "60",
            ["MarketData:BatchSize"] = "50",
            ["MarketData:RequestsPerMinute"] = "240",
            ["MarketData:LiveThresholdSeconds"] = "30",
            ["MarketData:StaleThresholdSeconds"] = "300",
            ["MarketData:ProviderTimeoutSeconds"] = "5",
            ["MarketData:Fake:Scenario"] = "normal",
            ["MarketData:Fake:Session"] = "regular",
            ["MarketData:Fake:DelayMinutes"] = "15",
            ["MarketData:Fake:RateLimitRetrySeconds"] = "1",
            ["MarketData:Fake:Currency"] = "USD",
            [$"{LearningOptions.SectionName}:ContentPath"] = Learning.LearningTestContent.WriteEmptyContent(),
        };
        if (marketDataSettings is not null)
        {
            foreach (var pair in marketDataSettings)
            {
                settings[pair.Key] = pair.Value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var environment = new TestHostEnvironment(environmentName);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddPlatformPersistence(configuration);
        services.AddIdentityModule(configuration);
        services.AddComplianceModule(configuration);
        services.AddLearningModule(configuration, environment);
        services.AddLedgerModule(configuration);
        services.AddCatalogModule(configuration);
        services.AddMarketDataModule(configuration, environment);
        services.AddSingleton<TimeProvider>(clock);
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "AskVest.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

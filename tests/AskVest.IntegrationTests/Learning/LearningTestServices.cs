using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Learning;
using AskVest.Modules.Learning.Configuration;
using AskVest.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AskVest.IntegrationTests.Learning;

internal static class LearningTestServices
{
    public static ServiceProvider CreateProvider(
        PostgresFixture fixture,
        TimeProvider clock,
        string? contentDirectory = null,
        bool loadSampleContent = false,
        string environmentName = "Development")
    {
        string directory;
        if (contentDirectory is null)
        {
            directory = Path.Combine(Path.GetTempPath(), $"learning-content-{Guid.CreateVersion7():N}");
            LearningTestContent.WriteValidContent(directory);
        }
        else
        {
            directory = contentDirectory;
        }

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = fixture.OwnerConnectionString,
            [$"{LearningOptions.SectionName}:ContentPath"] = directory,
            [$"{LearningOptions.SectionName}:LoadSampleContent"] = loadSampleContent ? "true" : "false",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var environment = new TestHostEnvironment(environmentName);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddPlatformPersistence(configuration);
        services.AddLearningModule(configuration, environment);
        services.AddSingleton<TimeProvider>(clock);
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "AskVest.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

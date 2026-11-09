using AskVest.Modules.Assistant.Configuration;
using AskVest.Modules.Assistant.Contracts;
using AskVest.Modules.Assistant.Domain;
using AskVest.Modules.Assistant.Persistence;
using AskVest.Modules.Assistant.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Assistant;

public static class AssistantModuleExtensions
{
    public static IServiceCollection AddAssistantModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddSingleton<IValidateOptions<AssistantOptions>>(AssistantOptionsValidator.Instance);
        services.AddOptions<AssistantOptions>()
            .Bind(configuration.GetSection(AssistantOptions.SectionName))
            .ValidateOnStart();

        var settings = configuration.GetSection(AssistantOptions.SectionName).Get<AssistantOptions>()
            ?? new AssistantOptions();
        if (!string.Equals(settings.Provider, "fake", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Assistant:Provider '{settings.Provider}' has no adapter yet; only 'fake' is implemented.");
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "The fake assistant model cannot run in Production; a real provider adapter and evaluation are required first.");
        }

        services.AddSingleton<FakeLanguageModelClient>();
        services.AddSingleton<ILanguageModelClient>(provider => provider.GetRequiredService<FakeLanguageModelClient>());
        services.AddSingleton<AssistantContent>();
        services.AddScoped<Agent.AssistantAgent>();
        services.AddHostedService<AssistantStartupCheck>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<AssistantTool, SearchInstrumentsTool>();
        services.AddScoped<AssistantTool, GetInstrumentTool>();
        services.AddScoped<AssistantTool, GetTokenDisclosureTool>();
        services.AddScoped<AssistantTool, GetPricesTool>();
        services.AddScoped<AssistantTool, GetPriceHistorySummaryTool>();
        services.AddScoped<AssistantTool, GetPortfolioTool>();
        services.AddScoped<AssistantTool, GetWatchlistTool>();
        services.AddScoped<AssistantTool, SearchLessonsTool>();
        services.AddScoped<AssistantTool, ProposeActionTool>();
        services.AddScoped<AssistantToolSet>();

        var connectionString = configuration.GetConnectionString("Assistant")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Assistant or ConnectionStrings:Platform is required.");

        services.AddDbContext<AssistantDbContext>((IServiceProvider provider, DbContextOptionsBuilder optionsBuilder) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            var scope = provider.GetService<AskVest.Platform.ITransactionScope>();
            AssistantDbContextOptions.Configure(optionsBuilder, connectionString, platformOptions.CommandTimeoutSeconds, scope?.Connection);
        });

        return services;
    }
}

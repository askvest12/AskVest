using AskVest.Modules.Assistant.Configuration;
using AskVest.Modules.Assistant.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Assistant;

public static class AssistantModuleExtensions
{
    public static IServiceCollection AddAssistantModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<AssistantOptions>>(AssistantOptionsValidator.Instance);
        services.AddOptions<AssistantOptions>()
            .Bind(configuration.GetSection(AssistantOptions.SectionName))
            .ValidateOnStart();

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

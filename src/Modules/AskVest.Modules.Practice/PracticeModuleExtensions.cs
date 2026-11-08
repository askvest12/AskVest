using AskVest.Modules.Practice.Configuration;
using AskVest.Modules.Practice.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Practice;

public static class PracticeModuleExtensions
{
    public static IServiceCollection AddPracticeModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Practice")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Practice or ConnectionStrings:Platform is required.");

        services.AddSingleton<IValidateOptions<PracticeOptions>>(PracticeOptions.Validator);
        services.AddOptions<PracticeOptions>()
            .Bind(configuration.GetSection(PracticeOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<PracticeDbContext>((IServiceProvider provider, DbContextOptionsBuilder options) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            var scope = provider.GetService<AskVest.Platform.ITransactionScope>();
            PracticeDbContextOptions.Configure(options, connectionString, platformOptions.CommandTimeoutSeconds, scope?.Connection);
        });

        return services;
    }
}

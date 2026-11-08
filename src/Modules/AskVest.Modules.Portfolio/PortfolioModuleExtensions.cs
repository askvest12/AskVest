using AskVest.Modules.Portfolio.Configuration;
using AskVest.Modules.Portfolio.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Portfolio;

public static class PortfolioModuleExtensions
{
    public static IServiceCollection AddPortfolioModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<PortfolioOptions>>(PortfolioOptionsValidator.Instance);
        services.AddOptions<PortfolioOptions>()
            .Bind(configuration.GetSection(PortfolioOptions.SectionName))
            .ValidateOnStart();

        var connectionString = configuration.GetConnectionString("Portfolio")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Portfolio or ConnectionStrings:Platform is required.");

        services.AddDbContext<PortfolioDbContext>((IServiceProvider provider, DbContextOptionsBuilder optionsBuilder) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            var scope = provider.GetService<AskVest.Platform.ITransactionScope>();
            PortfolioDbContextOptions.Configure(optionsBuilder, connectionString, platformOptions.CommandTimeoutSeconds, scope?.Connection);
        });

        return services;
    }
}

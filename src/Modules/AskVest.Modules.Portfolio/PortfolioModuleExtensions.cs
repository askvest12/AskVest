using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AskVest.Modules.Portfolio;

public static class PortfolioModuleExtensions
{
    public static IServiceCollection AddPortfolioModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services;
    }
}

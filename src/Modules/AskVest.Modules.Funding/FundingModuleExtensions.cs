using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AskVest.Modules.Funding;

public static class FundingModuleExtensions
{
    public static IServiceCollection AddFundingModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        return services;
    }
}

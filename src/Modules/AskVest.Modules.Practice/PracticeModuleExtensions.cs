using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AskVest.Modules.Practice;

public static class PracticeModuleExtensions
{
    public static IServiceCollection AddPracticeModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}

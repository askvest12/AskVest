using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AskVest.Modules.Learning;

public static class LearningModuleExtensions
{
    public static IServiceCollection AddLearningModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}

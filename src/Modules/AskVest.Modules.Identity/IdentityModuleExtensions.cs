using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AskVest.Modules.Identity;

public static class IdentityModuleExtensions
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        return services;
    }
}

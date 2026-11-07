using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AskVest.Modules.Ledger;

public static class LedgerModuleExtensions
{
    public static IServiceCollection AddLedgerModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        return services;
    }
}

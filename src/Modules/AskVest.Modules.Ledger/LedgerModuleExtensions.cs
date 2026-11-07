using AskVest.Modules.Ledger.Configuration;
using AskVest.Modules.Ledger.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Ledger;

public static class LedgerModuleExtensions
{
    public static IServiceCollection AddLedgerModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        var connectionString = configuration.GetConnectionString("Ledger")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Ledger or ConnectionStrings:Platform is required.");

        services.AddOptions<LedgerOptions>()
            .Bind(configuration.GetSection(LedgerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<LedgerDbContext>((IServiceProvider provider, DbContextOptionsBuilder options) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            var scope = provider.GetService<AskVest.Platform.ITransactionScope>();
            LedgerDbContextOptions.Configure(options, connectionString, platformOptions.CommandTimeoutSeconds, scope?.Connection);
        });

        return services;
    }
}

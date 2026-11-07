using AskVest.Modules.Catalog.Chains;
using AskVest.Modules.Catalog.Chains.Evm;
using AskVest.Modules.Catalog.Configuration;
using AskVest.Modules.Catalog.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Catalog;

public static class CatalogModuleExtensions
{
    public static IServiceCollection AddCatalogModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Catalog")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Catalog or ConnectionStrings:Platform is required.");

        services.AddOptions<CatalogOptions>()
            .Bind(configuration.GetSection(CatalogOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IChainFamilyHandler, EvmChainFamilyHandler>();
        services.AddSingleton<ChainFamilyRegistry>();
        services.AddScoped<Import.CatalogImportValidator>();
        services.AddScoped<Import.CatalogImportService>();

        services.AddDbContext<CatalogDbContext>((IServiceProvider provider, DbContextOptionsBuilder options) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            var scope = provider.GetService<AskVest.Platform.ITransactionScope>();
            CatalogDbContextOptions.Configure(options, connectionString, platformOptions.CommandTimeoutSeconds, scope?.Connection);
        });

        return services;
    }
}

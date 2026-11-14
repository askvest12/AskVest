using AskVest.Modules.Wallets.Configuration;
using AskVest.Modules.Wallets.Custody;
using AskVest.Modules.Wallets.Persistence;
using AskVest.Modules.Wallets.Screening;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets;

public static class WalletsModuleExtensions
{
    public static IServiceCollection AddWalletsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        var settings = configuration.GetSection(WalletsOptions.SectionName).Get<WalletsOptions>()
            ?? new WalletsOptions();
        var usesFake = settings.Custody.Routes.Any(route =>
                string.Equals(route.Provider, FakeCustodyProvider.ProviderKey, StringComparison.Ordinal))
            || string.Equals(settings.Screening.Provider, FakeAddressScreeningProvider.ProviderKey, StringComparison.Ordinal);
        if (environment?.IsProduction() == true && usesFake)
        {
            throw new InvalidOperationException(
                "The fake custody and screening providers cannot run in Production; real adapters are required first.");
        }

        var connectionString = configuration.GetConnectionString("Wallets")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Wallets or ConnectionStrings:Platform is required.");

        services.AddSingleton<IValidateOptions<WalletsOptions>>(WalletsOptions.Validator);
        services.AddOptions<WalletsOptions>()
            .Bind(configuration.GetSection(WalletsOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<FakeCustodyProvider>();
        services.AddSingleton<CustodyRegistry>();
        services.AddSingleton<FakeAddressScreeningProvider>();
        services.AddSingleton<IAddressScreeningProvider>(provider =>
            provider.GetRequiredService<FakeAddressScreeningProvider>());

        services.AddDbContext<WalletsDbContext>((IServiceProvider provider, DbContextOptionsBuilder options) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            var scope = provider.GetService<AskVest.Platform.ITransactionScope>();
            WalletsDbContextOptions.Configure(options, connectionString, platformOptions.CommandTimeoutSeconds, scope?.Connection);
        });

        return services;
    }
}

using AskVest.Modules.MarketData.Configuration;
using AskVest.Modules.MarketData.Persistence;
using AskVest.Modules.MarketData.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.MarketData;

public static class MarketDataModuleExtensions
{
    public static IServiceCollection AddMarketDataModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        GuardProductionProviders(configuration, environment);
        var connectionString = configuration.GetConnectionString("MarketData")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:MarketData or ConnectionStrings:Platform is required.");

        services.AddSingleton<IValidateOptions<MarketDataOptions>>(MarketDataOptions.Validator);
        services.AddOptions<MarketDataOptions>()
            .Bind(configuration.GetSection(MarketDataOptions.SectionName))
            .ValidateOnStart();
        services.PostConfigure<MarketDataOptions>(options => options.ApplyIntervalDefaults());
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<FakeMarketDataProvider>();
        services.AddSingleton<IMarketDataProvider>(provider => ProviderRegistry.Create(provider));
        services.AddSingleton<Services.PricePoller>();
        services.AddHostedService(provider => provider.GetRequiredService<Services.PricePoller>());
        services.AddSingleton<System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim>>(
            _ => new System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim>(StringComparer.Ordinal));
        services.AddScoped<Services.MarketDataQueryService>();
        services.AddScoped<Services.CandleService>();
        services.AddScoped<Contracts.IMarketData>(provider => provider.GetRequiredService<Services.MarketDataQueryService>());

        services.AddDbContext<MarketDataDbContext>((IServiceProvider provider, DbContextOptionsBuilder options) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            var scope = provider.GetService<AskVest.Platform.ITransactionScope>();
            MarketDataDbContextOptions.Configure(options, connectionString, platformOptions.CommandTimeoutSeconds, scope?.Connection);
        });

        return services;
    }

    private static void GuardProductionProviders(IConfiguration configuration, IHostEnvironment? environment)
    {
        if (environment?.IsProduction() != true)
        {
            return;
        }

        var provider = configuration[$"{MarketDataOptions.SectionName}:Provider"] ?? ProviderRegistry.FakeKey;
        if (string.Equals(provider, ProviderRegistry.FakeKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "MarketData:Provider=fake is not allowed in Production. Configure a licensed real provider before enabling market data.");
        }
    }
}

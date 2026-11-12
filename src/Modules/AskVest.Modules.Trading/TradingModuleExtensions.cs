using AskVest.Modules.Trading.Configuration;
using AskVest.Modules.Trading.Gateway;
using AskVest.Modules.Trading.Persistence;
using AskVest.Modules.Trading.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Trading;

public static class TradingModuleExtensions
{
    public static IServiceCollection AddTradingModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        var settings = configuration.GetSection(TradingOptions.SectionName).Get<TradingOptions>()
            ?? new TradingOptions();
        if (environment?.IsProduction() == true
            && settings.Gateway.Routes.Values.Any(route =>
                string.Equals(route.Provider, FakeIssuerGateway.ProviderName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The fake issuer gateway cannot run in Production; a real adapter is required first.");
        }

        var connectionString = configuration.GetConnectionString("Trading")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Trading or ConnectionStrings:Platform is required.");

        services.AddSingleton<IValidateOptions<TradingOptions>>(TradingOptions.Validator);
        services.AddOptions<TradingOptions>()
            .Bind(configuration.GetSection(TradingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssembly(typeof(TradingModuleExtensions).Assembly);

        services.AddSingleton<FakeIssuerGateway>();
        services.AddSingleton<GatewayRegistry>();
        services.AddScoped<TradingEventWriter>();

        services.AddDbContext<TradingDbContext>((IServiceProvider provider, DbContextOptionsBuilder options) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            var scope = provider.GetService<AskVest.Platform.ITransactionScope>();
            TradingDbContextOptions.Configure(options, connectionString, platformOptions.CommandTimeoutSeconds, scope?.Connection);
        });

        return services;
    }
}

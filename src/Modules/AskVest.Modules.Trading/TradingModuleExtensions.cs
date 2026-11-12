using AskVest.Modules.Trading.Configuration;
using FluentValidation;
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
                string.Equals(route.Provider, "fake", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The fake issuer gateway cannot run in Production; a real adapter is required first.");
        }

        services.AddSingleton<IValidateOptions<TradingOptions>>(TradingOptions.Validator);
        services.AddOptions<TradingOptions>()
            .Bind(configuration.GetSection(TradingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssembly(typeof(TradingModuleExtensions).Assembly);

        return services;
    }
}

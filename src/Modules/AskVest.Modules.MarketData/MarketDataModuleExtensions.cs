using Microsoft.Extensions.DependencyInjection;

namespace AskVest.Modules.MarketData;

public static class MarketDataModuleExtensions
{
    public static IServiceCollection AddMarketDataModule(this IServiceCollection services)
        => services;
}

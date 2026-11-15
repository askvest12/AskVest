using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Persistence;
using AskVest.Modules.Funding.Providers;
using AskVest.Modules.Funding.Services;
using AskVest.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Funding;

public static class FundingModuleExtensions
{
    public static IServiceCollection AddFundingModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var settings = configuration.GetSection(FundingOptions.SectionName).Get<FundingOptions>()
            ?? new FundingOptions();
        var usesFake = settings.Providers.Any(provider =>
                string.Equals(provider.Adapter, "fake", StringComparison.Ordinal))
            || string.Equals(settings.FxProvider, "fake", StringComparison.Ordinal);
        if (environment.IsProduction() && usesFake)
        {
            throw new InvalidOperationException(
                "The fake funding and FX providers cannot run in Production; real adapters are required first.");
        }

        var connectionString = configuration.GetConnectionString("Funding")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Funding or ConnectionStrings:Platform is required.");

        services.AddSingleton<IValidateOptions<FundingOptions>>(FundingOptions.Validator);
        services.AddOptions<FundingOptions>()
            .Bind(configuration.GetSection(FundingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<FundingDbContext>((IServiceProvider provider, DbContextOptionsBuilder options) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            var scope = provider.GetService<ITransactionScope>();
            FundingDbContextOptions.Configure(options, connectionString, platformOptions.CommandTimeoutSeconds, scope?.Connection);
        });

        services.AddSingleton<FakeFundingState>();
        services.AddSingleton<FakeFxRateProvider>();
        services.AddSingleton<IFxRateProvider>(provider => provider.GetRequiredService<FakeFxRateProvider>());
        services.AddSingleton<FundingProviderRegistry>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<FundingOptions>>().Value;
            var timeProvider = provider.GetRequiredService<TimeProvider>();
            var state = provider.GetRequiredService<FakeFundingState>();
            var providers = new List<IFundingProvider>();
            foreach (var route in options.Providers)
            {
                providers.Add(route.Adapter == "fake"
                    ? new FakeFundingProvider(
                        route.Key,
                        route.WebhookSecret,
                        state,
                        timeProvider,
                        TimeSpan.FromSeconds(options.ProviderTimeoutSeconds),
                        route.SettlementBalances)
                    : throw new InvalidOperationException(
                        $"Funding provider adapter '{route.Adapter}' has no implementation."));
            }

            return new FundingProviderRegistry(providers);
        });

        services.AddScoped<FundingEventWriter>();
        services.AddScoped<FundingAccountService>();
        services.AddScoped<FundingDepositService>();
        services.AddScoped<FundingWebhookService>();
        services.AddScoped<FundingConversionService>();
        services.AddScoped<FundingBeneficiaryService>();
        services.AddScoped<FundingWithdrawalService>();
        services.AddScoped<FundingPayoutSubmitService>();
        services.AddScoped<FundingPayoutProgressService>();
        services.AddScoped<FundingReservesService>();
        services.AddScoped<AskVest.Modules.Funding.Contracts.IFundingOperator, FundingOperatorService>();
        services.AddScoped<IOutboxHandler, FundingPayoutSubmitHandler>();
        services.AddSingleton<FundingPayoutProgressJob>();
        services.AddHostedService(provider => provider.GetRequiredService<FundingPayoutProgressJob>());
        services.AddSingleton<FundingCleanupJob>();
        services.AddHostedService(provider => provider.GetRequiredService<FundingCleanupJob>());
        services.AddHostedService<FundingDepositJob>();

        return services;
    }
}

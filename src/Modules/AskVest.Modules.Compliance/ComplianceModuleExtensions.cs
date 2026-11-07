using AskVest.Modules.Compliance.Configuration;
using AskVest.Modules.Compliance.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Compliance;

public static class ComplianceModuleExtensions
{
    public static IServiceCollection AddComplianceModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        GuardProductionProviders(configuration, environment);
        var connectionString = configuration.GetConnectionString("Compliance")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Compliance or ConnectionStrings:Platform is required.");

        services.AddSingleton<IValidateOptions<ComplianceOptions>>(ComplianceOptions.Validator);
        services.AddOptions<ComplianceOptions>()
            .Bind(configuration.GetSection(ComplianceOptions.SectionName))
            .ValidateOnStart();

        services.AddDbContext<ComplianceDbContext>((IServiceProvider provider, DbContextOptionsBuilder options) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            ComplianceDbContextOptions.Configure(options, connectionString, platformOptions.CommandTimeoutSeconds);
        });

        return services;
    }

    private static void GuardProductionProviders(IConfiguration configuration, IHostEnvironment? environment)
    {
        if (environment?.IsDevelopment() == true || environment?.IsEnvironment("Test") == true)
        {
            return;
        }

        if (environment is null)
        {
            return;
        }

        var realMoneyCountries = configuration
            .GetSection($"{ComplianceOptions.SectionName}:RealMoneyCountries")
            .Get<string[]>() ?? [];
        if (realMoneyCountries.Length == 0)
        {
            return;
        }

        var kycProvider = configuration[$"{ComplianceOptions.SectionName}:Kyc:Provider"] ?? KycOptions.DefaultProvider;
        var screeningProvider = configuration[$"{ComplianceOptions.SectionName}:Screening:Provider"] ?? ScreeningOptions.DefaultProvider;
        var failures = new List<string>();
        if (string.Equals(kycProvider, "fake", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Compliance:Kyc:Provider=fake is not allowed outside Development and Test when real-money countries are configured.");
        }

        if (string.Equals(screeningProvider, "fake", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Compliance:Screening:Provider=fake is not allowed outside Development and Test when real-money countries are configured.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", failures));
        }
    }
}

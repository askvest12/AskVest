using AskVest.Modules.Compliance.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Compliance;

public static class ComplianceModuleExtensions
{
    public static IServiceCollection AddComplianceModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Compliance")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Compliance or ConnectionStrings:Platform is required.");

        services.AddDbContext<ComplianceDbContext>((IServiceProvider provider, DbContextOptionsBuilder options) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            ComplianceDbContextOptions.Configure(options, connectionString, platformOptions.CommandTimeoutSeconds);
        });

        return services;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Platform is required.");

        services.AddOptions<PlatformOptions>()
            .Bind(configuration.GetSection(PlatformOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddScoped<ITransactionScope>(provider =>
            new TransactionScope(
                connectionString,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TransactionScope>>()));
        services.AddScoped<IOutbox, OutboxEnqueuer>();
        services.AddHostedService<OutboxDispatcher>();

        services.AddDbContext<PlatformDbContext>((IServiceProvider provider, DbContextOptionsBuilder options) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<PlatformOptions>>().Value;
            var scope = provider.GetService<ITransactionScope>();
            PlatformDbContextOptions.Configure(
                options,
                connectionString,
                platformOptions.CommandTimeoutSeconds,
                scope?.Connection);
        });

        services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("platform-database");
        return services;
    }
}

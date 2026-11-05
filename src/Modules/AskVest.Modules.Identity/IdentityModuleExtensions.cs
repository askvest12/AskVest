using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity;

public static class IdentityModuleExtensions
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Identity")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Identity or ConnectionStrings:Platform is required.");

        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<IdentityOptions>>(IdentityOptions.Validator);
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<IdentitySecretsOptions>>(IdentitySecretsOptions.Validator);

        services.AddOptions<IdentityOptions>()
            .Bind(configuration.GetSection(IdentityOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<IdentitySecretsOptions>()
            .Bind(configuration.GetSection(IdentitySecretsOptions.SectionName))
            .ValidateOnStart();

        services.AddDbContext<IdentityDbContext>((IServiceProvider provider, DbContextOptionsBuilder options) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            IdentityDbContextOptions.Configure(options, connectionString, platformOptions.CommandTimeoutSeconds);
        });

        return services;
    }
}

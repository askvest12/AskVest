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

        services.AddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.AddSingleton<AskVest.Modules.Identity.Crypto.Argon2idCore>();
        services.AddSingleton<AskVest.Modules.Identity.Crypto.CredentialHashingLimiter>();
        services.AddSingleton<AskVest.Modules.Identity.Crypto.IPasswordHasher, AskVest.Modules.Identity.Crypto.PasswordHasher>();
        services.AddSingleton<AskVest.Modules.Identity.Crypto.IPinHasher, AskVest.Modules.Identity.Crypto.PinHasher>();
        services.AddSingleton<AskVest.Modules.Identity.Crypto.ISecureRandom, AskVest.Modules.Identity.Crypto.SecureRandom>();
        services.AddSingleton<AskVest.Modules.Identity.Crypto.IOneTimeCodeHasher, AskVest.Modules.Identity.Crypto.OneTimeCodeHasher>();
        services.AddSingleton<AskVest.Modules.Identity.Crypto.IAccessTokenService, AskVest.Modules.Identity.Crypto.AccessTokenService>();
        services.AddSingleton<AskVest.Modules.Identity.Crypto.IBreachPasswordChecker, AskVest.Modules.Identity.Crypto.NullBreachPasswordChecker>();
        services.AddSingleton<AskVest.Modules.Identity.Crypto.IPasswordPolicy, AskVest.Modules.Identity.Crypto.PasswordPolicy>();

        return services;
    }
}

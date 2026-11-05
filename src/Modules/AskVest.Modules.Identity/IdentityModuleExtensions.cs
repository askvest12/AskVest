using FluentValidation;
using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;
using AskVest.Modules.Identity.Endpoints;
using AskVest.Modules.Identity.Messaging;
using AskVest.Modules.Identity.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity;

public static class IdentityModuleExtensions
{
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        GuardProductionMessaging(configuration, environment);
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

        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<MessagingOptions>>(MessagingOptions.Validator);
        services.AddOptions<MessagingOptions>()
            .Bind(configuration.GetSection(MessagingOptions.SectionName))
            .ValidateOnStart();

        var smtpHost = configuration[$"{MessagingOptions.SectionName}:Smtp:Host"];
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            services.AddSingleton<IEmailTransport, NullEmailTransport>();
        }
        else
        {
            services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
        }

        var smsMode = configuration[$"{MessagingOptions.SectionName}:Sms:Mode"];
        if (string.Equals(smsMode, nameof(SmsAdapterMode.Development), StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ISmsTransport, DevelopmentSmsTransport>();
        }
        else
        {
            services.AddSingleton<ISmsTransport, NullSmsTransport>();
        }

        services.AddScoped<SecurityEventRecorder>();
        services.AddScoped<OneTimeCodeService>();
        services.AddScoped<SignUpService>();
        services.AddScoped<EmailVerificationService>();
        services.AddValidatorsFromAssembly(typeof(IdentityModuleExtensions).Assembly);

        services.AddSingleton<MessageChannel>();
        services.AddSingleton<IMessageChannel>(provider => provider.GetRequiredService<MessageChannel>());
        services.AddHostedService<MessageDispatchWorker>();

        return services;
    }

    private static void GuardProductionMessaging(IConfiguration configuration, IHostEnvironment? environment)
    {
        if (environment?.IsProduction() != true)
        {
            return;
        }

        var failures = new List<string>();
        var smsMode = configuration[$"{MessagingOptions.SectionName}:Sms:Mode"];
        if (string.Equals(smsMode, nameof(SmsAdapterMode.Development), StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Messaging:Sms:Mode=Development is not allowed in Production.");
        }

        var smtpHost = configuration[$"{MessagingOptions.SectionName}:Smtp:Host"];
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            failures.Add("Messaging:Smtp:Host is required in Production.");
        }
        else if (IsDevelopmentMailHost(smtpHost))
        {
            failures.Add("Messaging:Smtp:Host points at a development mail sink (loopback or Mailpit); a real SMTP provider is required in Production.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Identity messaging configuration is invalid for Production: " + string.Join(" ", failures));
        }
    }

    private static bool IsDevelopmentMailHost(string host)
    {
        var normalized = host.Trim().ToLowerInvariant();
        return normalized is "localhost" or "127.0.0.1" or "::1" or "mailpit" or "[::1]";
    }
}

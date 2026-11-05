using AskVest.Api;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Messaging;
using AskVest.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AskVest.IntegrationTests.Identity;

public sealed class AuthApiFactory(
    string connectionString,
    IReadOnlyDictionary<string, string?>? extraSettings = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    public CapturingEmailTransport Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Platform", connectionString);
        builder.UseSetting("RateLimiting:Global:PermitLimit", "10000");
        builder.UseSetting("RateLimiting:Strict:PermitLimit", "10000");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = connectionString,
                ["Docs:Enabled"] = "false",
                ["RateLimiting:Global:PermitLimit"] = "10000",
                ["RateLimiting:Strict:PermitLimit"] = "10000",
                ["Identity:PasswordBreachCheckEnabled"] = "true",
            };
            if (extraSettings is not null)
            {
                foreach (var (key, value) in extraSettings)
                {
                    settings[key] = value;
                }
            }

            config.AddInMemoryCollection(settings);
            foreach (var (key, value) in TestIdentitySecrets.Entries)
            {
                config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
            }
        });
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Emails);
            services.Replace(ServiceDescriptor.Singleton<IEmailTransport>(
                _ => Emails));
            services.Replace(ServiceDescriptor.Singleton<IBreachPasswordChecker>(
                _ => new MarkerBreachChecker()));
            configureServices?.Invoke(services);
        });
    }

    private sealed class MarkerBreachChecker : IBreachPasswordChecker
    {
        public Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
            => Task.FromResult(password == "BreachedMarker99");
    }
}

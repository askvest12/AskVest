using AskVest.Api;
using AskVest.IntegrationTests.Api;
using AskVest.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Compliance;

public sealed class ComplianceConfigFactory(IReadOnlyDictionary<string, string?> overrides) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Platform", "Host=localhost;Port=1;Database=none;Username=none;Password=none");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = "Host=localhost;Port=1;Database=none;Username=none;Password=none",
                ["RateLimiting:Global:PermitLimit"] = "1000",
                ["RateLimiting:Strict:PermitLimit"] = "100",
                ["Docs:Enabled"] = "true",
            };
            foreach (var (key, value) in TestIdentitySecrets.Entries.Concat(TestComplianceSecrets.Entries))
            {
                settings[key] = value;
            }

            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }

            config.AddInMemoryCollection(settings);
        });
    }
}

[Collection(ApiCollectionDefinition.Name)]
public sealed class ComplianceConfigurationTests
{
    [Fact]
    public void Invalid_Compliance_Configuration_Prevents_Startup()
    {
        using var factory = new ComplianceConfigFactory(new Dictionary<string, string?>
        {
            ["Compliance:Kyc:WebhookSecret"] = "short",
        });

        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void Valid_Compliance_Configuration_Starts()
    {
        using var factory = new ComplianceConfigFactory(new Dictionary<string, string?>
        {
            ["Compliance:Documents:0:Type"] = "terms",
            ["Compliance:Documents:0:Version"] = "1",
            ["Compliance:Documents:0:Url"] = "https://askvest.example/terms",
            ["Compliance:Documents:0:Sha256"] = new string('a', 64),
            ["Compliance:Documents:0:RequiredFor"] = "practice",
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test", "1");
    }
}

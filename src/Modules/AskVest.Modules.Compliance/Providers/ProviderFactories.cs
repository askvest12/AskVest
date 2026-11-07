using AskVest.Modules.Compliance.Configuration;
using Microsoft.Extensions.Configuration;

namespace AskVest.Modules.Compliance.Providers;

/// <summary>
/// Selects provider implementations. Names are validated at startup through
/// the options validator; creation happens lazily on first use.
/// </summary>
internal static class ProviderFactories
{
    internal const string FakeProviderName = "fake";

    public static bool IsSupportedKycProvider(string? name)
        => string.Equals(name, FakeProviderName, StringComparison.OrdinalIgnoreCase);

    public static bool IsSupportedScreeningProvider(string? name)
        => string.Equals(name, FakeProviderName, StringComparison.OrdinalIgnoreCase);

    public static IKycProvider CreateKycProvider(IConfiguration configuration)
    {
        var name = configuration[$"{ComplianceOptions.SectionName}:Kyc:Provider"]
            ?? KycOptions.DefaultProvider;
        if (!IsSupportedKycProvider(name))
        {
            throw new InvalidOperationException($"Unknown Compliance:Kyc:Provider '{name}'.");
        }

        var secret = configuration[$"{ComplianceOptions.SectionName}:Kyc:WebhookSecret"]
            ?? throw new InvalidOperationException("Compliance:Kyc:WebhookSecret is required.");
        var tolerance = configuration.GetValue<TimeSpan>($"{ComplianceOptions.SectionName}:WebhookTolerance");
        if (tolerance <= TimeSpan.Zero)
        {
            tolerance = TimeSpan.FromMinutes(5);
        }

        return new FakeKycProvider(secret, tolerance);
    }

    public static IScreeningProvider CreateScreeningProvider(IConfiguration configuration)
    {
        var name = configuration[$"{ComplianceOptions.SectionName}:Screening:Provider"]
            ?? ScreeningOptions.DefaultProvider;
        if (!IsSupportedScreeningProvider(name))
        {
            throw new InvalidOperationException($"Unknown Compliance:Screening:Provider '{name}'.");
        }

        return new FakeScreeningProvider(
            configuration[$"{ComplianceOptions.SectionName}:Screening:HitNameMarker"]);
    }
}

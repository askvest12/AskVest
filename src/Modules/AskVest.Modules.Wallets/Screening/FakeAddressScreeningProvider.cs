using System.Security.Cryptography;
using System.Text;
using AskVest.Modules.Wallets.Configuration;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets.Screening;

/// <summary>
/// The deterministic in-memory screening provider used in development and
/// tests. Addresses containing a configured hit marker screen as hits; the
/// error scenario fails closed. Refused in Production at startup.
/// </summary>
internal sealed class FakeAddressScreeningProvider(IOptions<WalletsOptions> options)
    : IAddressScreeningProvider
{
    internal const string ProviderKey = "fake";

    public string ProviderName => ProviderKey;

    public Task<ScreeningResult> ScreenAsync(
        string chainId,
        string address,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fake = options.Value.Screening.Fake;
        if (string.Equals(fake.Scenario, "error", StringComparison.Ordinal))
        {
            return Task.FromResult(new ScreeningResult(ScreeningOutcomeKind.Error, null));
        }

        var hit = fake.HitMarkers.Any(marker =>
            !string.IsNullOrWhiteSpace(marker)
            && address.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (hit)
        {
            var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{chainId}|{address}")))[..16].ToLowerInvariant();
            return Task.FromResult(new ScreeningResult(ScreeningOutcomeKind.Hit, $"fake-screen-{digest}"));
        }

        return Task.FromResult(new ScreeningResult(ScreeningOutcomeKind.Clear, $"fake-screen-ok-{chainId}"));
    }

    /// <summary>
    /// Test and probe hook: overrides the configured scenario at runtime. The
    /// fake is refused in Production, so this can never arm a live provider.
    /// </summary>
    internal void SetScenario(string scenario)
        => options.Value.Screening.Fake.Scenario = scenario;
}

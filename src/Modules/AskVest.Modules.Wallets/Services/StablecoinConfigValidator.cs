using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Wallets.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets.Services;

/// <summary>
/// Validates every configured approved stablecoin once at startup through
/// the Catalog contract: the chain must exist and the contract must
/// canonicalize to itself. Any invalid entry stops the host.
/// </summary>
internal sealed class StablecoinConfigValidator(
    IServiceScopeFactory scopeFactory,
    IOptions<WalletsOptions> options,
    ApprovedStablecoins approved,
    ILogger<StablecoinConfigValidator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value.ApprovedStablecoins;
        if (configured.Count == 0)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalog>();
        var valid = new List<ApprovedStablecoinOption>(configured.Count);
        foreach (var entry in configured)
        {
            var canonical = await catalog.ValidateAddressAsync(
                entry.ChainId, entry.Contract, cancellationToken);
            if (canonical.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Wallets:ApprovedStablecoins entry for chain '{entry.ChainId}' failed Catalog address validation.");
            }

            valid.Add(new ApprovedStablecoinOption
            {
                ChainId = entry.ChainId,
                Contract = canonical.Value,
                LedgerAssetCode = entry.LedgerAssetCode,
                Decimals = entry.Decimals,
                DustMinimum = entry.DustMinimum,
            });
        }

        approved.Replace(valid);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Validated {Count} approved stablecoin contracts.", valid.Count);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using AskVest.SharedKernel;

namespace AskVest.Modules.Catalog.Contracts;

/// <summary>
/// The only catalog surface other modules may use. Lookups are read-only;
/// catalog content changes exclusively through reviewed data file imports.
/// </summary>
public interface ICatalog
{
    Task<CatalogToken?> FindTokenAsync(Guid tokenId, CancellationToken cancellationToken = default);

    Task<CatalogToken?> FindTokenByAssetCodeAsync(string assetCode, CancellationToken cancellationToken = default);

    Task<CatalogInstrument?> FindInstrumentAsync(string instrumentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogToken>> ListListedTokensForChainAsync(string chainId, CancellationToken cancellationToken = default);

    Task<CatalogChain?> FindChainAsync(string chainId, CancellationToken cancellationToken = default);

    Task<Result<string>> ValidateAddressAsync(string chainId, string address, CancellationToken cancellationToken = default);

    Task<TokenAvailability?> GetAvailabilityAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default);
}

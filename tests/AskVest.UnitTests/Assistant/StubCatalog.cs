using AskVest.Modules.Catalog.Contracts;
using AskVest.SharedKernel;

namespace AskVest.UnitTests.Assistant;

internal sealed class StubCatalog : ICatalog
{
    public List<CatalogInstrument> Instruments { get; } = [];

    public CatalogTokenDisclosure? Disclosure { get; set; }

    public Task<CatalogToken?> FindTokenAsync(Guid tokenId, CancellationToken cancellationToken = default)
        => Task.FromResult<CatalogToken?>(null);

    public Task<CatalogToken?> FindTokenByAssetCodeAsync(string assetCode, CancellationToken cancellationToken = default)
        => Task.FromResult<CatalogToken?>(null);

    public Task<CatalogInstrument?> FindInstrumentAsync(string instrumentId, CancellationToken cancellationToken = default)
        => Task.FromResult(Instruments.FirstOrDefault(
            instrument => string.Equals(instrument.Id, instrumentId, StringComparison.Ordinal)));

    public Task<IReadOnlyList<CatalogToken>> ListTokensForInstrumentAsync(
        string instrumentId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CatalogToken>>([]);

    public Task<CatalogTokenDisclosure?> FindTokenDisclosureAsync(Guid tokenId, CancellationToken cancellationToken = default)
        => Task.FromResult(Disclosure is not null && Disclosure.TokenId == tokenId ? Disclosure : null);

    public Task<IReadOnlyList<CatalogInstrument>> ListActiveInstrumentsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CatalogInstrument>>(
            Instruments.Where(instrument => instrument.Status == "active").ToList());

    public Task<IReadOnlyList<CatalogToken>> ListListedTokensForChainAsync(
        string chainId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CatalogToken>>([]);

    public Task<CatalogChain?> FindChainAsync(string chainId, CancellationToken cancellationToken = default)
        => Task.FromResult<CatalogChain?>(null);

    public Task<Result<string>> ValidateAddressAsync(string chainId, string address, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success(address));

    public Task<TokenAvailability?> GetAvailabilityAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default)
        => Task.FromResult<TokenAvailability?>(null);
}

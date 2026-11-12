using AskVest.Modules.Catalog.Chains;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Catalog.Domain;
using AskVest.Modules.Catalog.Domain.Enums;
using AskVest.Modules.Catalog.Persistence;
using AskVest.Modules.Catalog.Persistence.Entities;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Catalog.Services;

/// <summary>
/// The ICatalog implementation other modules consume. Every lookup is a
/// read-only projection into the contracts namespace.
/// </summary>
internal sealed class CatalogService(
    CatalogDbContext db,
    ChainFamilyRegistry chainFamilies,
    CatalogAvailabilityService availability) : ICatalog
{
    public async Task<CatalogToken?> FindTokenAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        var token = await db.Tokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken);
        return token is null ? null : ToContract(token);
    }

    public async Task<CatalogToken?> FindTokenByAssetCodeAsync(string assetCode, CancellationToken cancellationToken = default)
    {
        var normalized = assetCode.Trim().ToUpperInvariant();
        var token = await db.Tokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.LedgerAssetCode.Equals(normalized), cancellationToken);
        return token is null ? null : ToContract(token);
    }

    public async Task<IReadOnlyList<CatalogToken>> ListTokensForInstrumentAsync(
        string instrumentId,
        CancellationToken cancellationToken = default)
    {
        var tokens = await db.Tokens.AsNoTracking()
            .Where(t => t.InstrumentId == instrumentId)
            .OrderBy(t => t.Symbol)
            .ToListAsync(cancellationToken);
        return tokens.Select(ToContract).ToList();
    }

    public async Task<CatalogTokenDisclosure?> FindTokenDisclosureAsync(
        Guid tokenId,
        CancellationToken cancellationToken = default)
    {
        var token = await db.Tokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken);
        return token is null ? null : new CatalogTokenDisclosure(
            token.Id,
            token.InstrumentId,
            token.Symbol,
            token.ChainId,
            CatalogText.ToDb(token.Status),
            CatalogText.ToDb(token.ClaimType),
            token.ClaimDescription,
            token.BackingDescription,
            token.CustodianDescription,
            token.RedemptionDescription,
            CatalogText.ToDb(token.DividendPolicy),
            token.DividendNote,
            token.VotingRights,
            token.VotingNote,
            token.TransferRestrictions,
            token.IssuerFees,
            token.NoIssuerFees,
            token.RiskStatements,
            token.DisclosureDocumentLink,
            token.ReviewedAt,
            token.ReviewExpiresAt);
    }

    public async Task<CatalogInstrument?> FindInstrumentAsync(string instrumentId, CancellationToken cancellationToken = default)
    {
        var instrument = await db.Instruments.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == instrumentId, cancellationToken);
        return instrument is null ? null : ToContract(instrument);
    }

    public async Task<IReadOnlyList<CatalogInstrument>> ListActiveInstrumentsAsync(CancellationToken cancellationToken = default)
    {
        var instruments = await db.Instruments.AsNoTracking()
            .Where(i => i.Status == InstrumentStatus.Active)
            .OrderBy(i => i.Id)
            .ToListAsync(cancellationToken);
        return instruments.Select(ToContract).ToList();
    }

    public async Task<IReadOnlyList<CatalogToken>> ListListedTokensForChainAsync(
        string chainId,
        CancellationToken cancellationToken = default)
    {
        var tokens = await db.Tokens.AsNoTracking()
            .Where(t => t.ChainId == chainId && t.Status == TokenStatus.Listed)
            .OrderBy(t => t.Symbol)
            .ToListAsync(cancellationToken);
        return tokens.Select(ToContract).ToList();
    }

    public async Task<CatalogChain?> FindChainAsync(string chainId, CancellationToken cancellationToken = default)
    {
        var chain = await db.Chains.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == chainId, cancellationToken);
        return chain is null ? null : ToContract(chain);
    }

    public async Task<Result<string>> ValidateAddressAsync(
        string chainId,
        string address,
        CancellationToken cancellationToken = default)
    {
        var chain = await db.Chains.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == chainId, cancellationToken);
        if (chain is null)
        {
            return Result.Failure<string>(new Error(
                ErrorCodes.ResourceNotFound,
                $"Chain '{chainId}' does not exist.",
                ErrorType.NotFound));
        }

        if (!chainFamilies.TryGet(chain.Family, out var handler) || handler is null)
        {
            return Result.Failure<string>(new Error(
                ErrorCodes.CatalogAddressInvalid,
                $"Chain family '{chain.Family}' has no handler.",
                ErrorType.BusinessRule));
        }

        return handler.CanonicalizeAddress(chainId, address);
    }

    public Task<TokenAvailability?> GetAvailabilityAsync(
        Guid userId,
        Guid tokenId,
        CancellationToken cancellationToken = default)
        => availability.GetAsync(userId, tokenId, cancellationToken);

    internal static CatalogToken ToContract(Token token) => new(
        token.Id,
        token.InstrumentId,
        token.IssuerId,
        token.ChainId,
        token.Standard,
        token.CanonicalAddress,
        token.Caip19,
        token.Symbol,
        token.Decimals,
        token.LedgerAssetCode,
        CatalogText.ToDb(token.Status),
        token.GatewayKey,
        CatalogText.ToDb(token.ScheduleKind),
        token.ReviewedAt,
        token.ReviewExpiresAt);

    internal static CatalogInstrument ToContract(Instrument instrument) => new(
        instrument.Id,
        CatalogText.ToDb(instrument.Kind),
        instrument.Name,
        instrument.Symbol,
        instrument.Isin,
        instrument.ListingExchange,
        instrument.CountryOfListing,
        instrument.QuoteCurrency,
        instrument.Sector,
        CatalogText.ToDb(instrument.Status),
        instrument.ShortDescription);

    internal static CatalogChain ToContract(Chain chain) => new(
        chain.Id,
        chain.Family,
        chain.Name,
        chain.NativeAssetSymbol,
        chain.NativeDecimals,
        chain.ExplorerLinkTemplate,
        chain.RequiredConfirmations,
        CatalogText.ToDb(chain.Status));
}

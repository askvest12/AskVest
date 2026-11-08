using AskVest.Modules.Catalog.Chains;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Catalog.Domain.Enums;
using AskVest.Modules.Catalog.Persistence;
using AskVest.Modules.Catalog.Persistence.Entities;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Catalog.Services;

/// <summary>
/// Read-side queries for the catalog endpoints. Everything returned is
/// catalog data; availability is computed per caller.
/// </summary>
internal sealed class CatalogReadService(
    CatalogDbContext db,
    CatalogAvailabilityService availability,
    ChainFamilyRegistry chainFamilies)
{
    public async Task<PageResult<Instrument>> GetInstrumentPageAsync(
        int limit,
        string? cursor,
        InstrumentKind? kind,
        string? search,
        CancellationToken cancellationToken)
    {
        var query = db.Instruments.AsNoTracking().Where(i => i.Status == InstrumentStatus.Active);
        if (kind is not null)
        {
            query = query.Where(i => i.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(i => EF.Functions.ILike(i.Symbol, term + "%")
                || EF.Functions.ILike(i.Name, "%" + term + "%"));
        }

        // Cursor comparison and ordering stay in memory with Ordinal semantics:
        // EF cannot translate string keyset predicates, and the active
        // instrument set is small and bounded.
        var hasCursor = TryDecodeCursor(cursor, out var cursorSymbol, out var cursorId);
        var page = (await query.ToListAsync(cancellationToken))
            .OrderBy(i => i.Symbol, StringComparer.Ordinal)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .Where(i => !hasCursor
                || string.CompareOrdinal(i.Symbol, cursorSymbol) > 0
                || (string.Equals(i.Symbol, cursorSymbol, StringComparison.Ordinal)
                    && string.CompareOrdinal(i.Id, cursorId) > 0))
            .Take(limit + 1)
            .ToList();

        var hasMore = page.Count > limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        var nextCursor = hasMore && page.Count > 0
            ? CursorCodec.Encode($"{page[^1].Symbol}|{page[^1].Id}")
            : null;
        return new PageResult<Instrument>(page, nextCursor, hasMore, limit);
    }

    public async Task<InstrumentDetail?> GetInstrumentDetailAsync(
        string instrumentId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var instrument = await db.Instruments.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == instrumentId, cancellationToken);
        if (instrument is null)
        {
            return null;
        }

        var tokens = await db.Tokens.AsNoTracking()
            .Where(t => t.InstrumentId == instrumentId)
            .OrderBy(t => t.Symbol)
            .ToListAsync(cancellationToken);

        var items = new List<TokenListItem>(tokens.Count);
        foreach (var token in tokens)
        {
            var tokenAvailability = await availability.GetAsync(userId, token.Id, cancellationToken);
            items.Add(new TokenListItem(token, tokenAvailability));
        }

        return new InstrumentDetail(instrument, items);
    }

    public async Task<TokenDetail?> GetTokenDetailAsync(
        Guid tokenId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var token = await db.Tokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken);
        if (token is null)
        {
            return null;
        }

        var issuer = await db.Issuers.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == token.IssuerId, cancellationToken);
        var chain = await db.Chains.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == token.ChainId, cancellationToken);
        var countries = await db.TokenCountries.AsNoTracking()
            .Where(tc => tc.TokenId == tokenId)
            .Select(tc => tc.CountryCode)
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);
        var tokenAvailability = await availability.GetAsync(userId, tokenId, cancellationToken);

        string? explorerLink = null;
        if (chain is not null
            && chainFamilies.TryGet(chain.Family, out var handler)
            && handler is not null)
        {
            explorerLink = handler.BuildExplorerLink(chain.Id, token.CanonicalAddress, chain.ExplorerLinkTemplate);
        }

        return new TokenDetail(token, issuer, chain, countries, tokenAvailability, explorerLink);
    }

    public async Task<IReadOnlyList<Chain>> ListEnabledChainsAsync(CancellationToken cancellationToken)
        => await db.Chains.AsNoTracking()
            .Where(c => c.Status == ChainStatus.Enabled)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);

    private static bool TryDecodeCursor(string? cursor, out string symbol, out string id)
    {
        symbol = string.Empty;
        id = string.Empty;
        if (cursor is null || !CursorCodec.TryDecode(cursor, out var payload) || payload is null)
        {
            return false;
        }

        var separator = payload.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        symbol = payload[..separator];
        id = payload[(separator + 1)..];
        return id.Length > 0;
    }
}

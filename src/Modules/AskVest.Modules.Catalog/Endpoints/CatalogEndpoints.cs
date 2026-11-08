using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Catalog.Domain;
using AskVest.Modules.Catalog.Domain.Enums;
using AskVest.Modules.Catalog.Persistence.Entities;
using AskVest.Modules.Catalog.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Catalog.Endpoints;

public static class CatalogEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var catalog = api.MapGroup("/catalog").WithTags("Catalog");

        catalog.MapGet("/instruments", (HttpContext httpContext, CatalogReadService reads,
                int? limit, string? cursor, string? kind, string? search, CancellationToken cancellationToken) =>
                ListInstrumentsAsync(httpContext, reads, limit, cursor, kind, search, cancellationToken))
            .WithName("ListCatalogInstruments")
            .WithSummary("Active catalog instruments, paginated, with kind and search filters.");

        catalog.MapGet("/instruments/{instrumentId}", (HttpContext httpContext, CatalogReadService reads,
                string instrumentId, CancellationToken cancellationToken) =>
                GetInstrumentAsync(httpContext, reads, instrumentId, cancellationToken))
            .WithName("GetCatalogInstrument")
            .WithSummary("One instrument with its tokens and the caller's availability.");

        catalog.MapGet("/tokens/{tokenId:guid}", (HttpContext httpContext, CatalogReadService reads,
                Guid tokenId, CancellationToken cancellationToken) =>
                GetTokenAsync(httpContext, reads, tokenId, cancellationToken))
            .WithName("GetCatalogToken")
            .WithSummary("Full token disclosure view with the caller's availability.");

        catalog.MapGet("/chains", (HttpContext httpContext, CatalogReadService reads,
                CancellationToken cancellationToken) =>
                ListChainsAsync(httpContext, reads, cancellationToken))
            .WithName("ListCatalogChains")
            .WithSummary("Enabled catalog chains.");
    }

    private static async Task<IResult> ListInstrumentsAsync(
        HttpContext httpContext,
        CatalogReadService reads,
        int? limit,
        string? cursor,
        string? kind,
        string? search,
        CancellationToken cancellationToken)
    {
        InstrumentKind? parsedKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            parsedKind = kind.Trim().ToLowerInvariant() switch
            {
                "stock" => InstrumentKind.Stock,
                "etf" => InstrumentKind.Etf,
                _ => null,
            };
            if (parsedKind is null)
            {
                return CatalogResults.Error(new Error(
                    ErrorCodes.ValidationFailed,
                    "The kind filter must be 'stock' or 'etf'.",
                    ErrorType.Validation), httpContext);
            }
        }

        var page = await reads.GetInstrumentPageAsync(
            Math.Clamp(limit ?? PageRequest.DefaultLimit, 1, PageRequest.MaxLimit),
            cursor,
            parsedKind,
            search,
            cancellationToken);

        return CatalogResults.Ok(
            new
            {
                items = page.Items.Select(InstrumentSummary),
                nextCursor = page.NextCursor,
                hasMore = page.HasMore,
                limit = page.Limit,
            },
            "Instruments loaded.",
            httpContext);
    }

    private static async Task<IResult> GetInstrumentAsync(
        HttpContext httpContext,
        CatalogReadService reads,
        string instrumentId,
        CancellationToken cancellationToken)
    {
        var detail = await reads.GetInstrumentDetailAsync(
            instrumentId, CurrentUserId(httpContext), cancellationToken);
        if (detail is null)
        {
            return NotFound(httpContext);
        }

        return CatalogResults.Ok(
            new
            {
                instrument = InstrumentSummary(detail.Instrument),
                tokens = detail.Tokens.Select(item => new
                {
                    id = item.Token.Id,
                    chainId = item.Token.ChainId,
                    symbol = item.Token.Symbol,
                    status = CatalogText.ToDb(item.Token.Status),
                    ledgerAssetCode = item.Token.LedgerAssetCode,
                    caip19 = item.Token.Caip19,
                    availability = AvailabilityView(item.Availability),
                }),
            },
            "Instrument loaded.",
            httpContext);
    }

    private static async Task<IResult> GetTokenAsync(
        HttpContext httpContext,
        CatalogReadService reads,
        Guid tokenId,
        CancellationToken cancellationToken)
    {
        var detail = await reads.GetTokenDetailAsync(
            tokenId, CurrentUserId(httpContext), cancellationToken);
        if (detail is null)
        {
            return NotFound(httpContext);
        }

        var token = detail.Token;
        return CatalogResults.Ok(
            new
            {
                id = token.Id,
                instrumentId = token.InstrumentId,
                issuer = detail.Issuer is null
                    ? null
                    : new
                    {
                        id = detail.Issuer.Id,
                        legalName = detail.Issuer.LegalName,
                        jurisdiction = detail.Issuer.Jurisdiction,
                        website = detail.Issuer.Website,
                        regulatoryStatus = detail.Issuer.RegulatoryStatus,
                        legalDocumentLinks = detail.Issuer.LegalDocumentLinks,
                        status = CatalogText.ToDb(detail.Issuer.Status),
                    },
                chain = detail.Chain is null
                    ? null
                    : new
                    {
                        id = detail.Chain.Id,
                        family = detail.Chain.Family,
                        name = detail.Chain.Name,
                    },
                standard = token.Standard,
                contractAddress = token.CanonicalAddress,
                caip19 = token.Caip19,
                ledgerAssetCode = token.LedgerAssetCode,
                symbol = token.Symbol,
                decimals = token.Decimals,
                status = CatalogText.ToDb(token.Status),
                allowedCountries = detail.Countries,
                explorerLink = detail.ExplorerLink,
                claim = new
                {
                    type = CatalogText.ToDb(token.ClaimType),
                    description = token.ClaimDescription,
                },
                backing = token.BackingDescription,
                custodian = token.CustodianDescription,
                redemptionAndExit = token.RedemptionDescription,
                dividendPolicy = new
                {
                    policy = CatalogText.ToDb(token.DividendPolicy),
                    note = token.DividendNote,
                },
                votingRights = new
                {
                    allowed = token.VotingRights,
                    note = token.VotingNote,
                },
                transferRestrictions = token.TransferRestrictions,
                issuerFees = token.NoIssuerFees ? new List<string>() : token.IssuerFees,
                noIssuerFees = token.NoIssuerFees,
                riskStatements = token.RiskStatements,
                disclosureDocumentLink = token.DisclosureDocumentLink,
                tradingSchedule = new
                {
                    kind = CatalogText.ToDb(token.ScheduleKind),
                    timeZone = token.ScheduleTimeZone,
                    note = token.ScheduleNote,
                },
                reviewedAt = token.ReviewedAt,
                reviewExpiresAt = token.ReviewExpiresAt,
                availability = AvailabilityView(detail.Availability),
            },
            "Token loaded.",
            httpContext);
    }

    private static async Task<IResult> ListChainsAsync(
        HttpContext httpContext,
        CatalogReadService reads,
        CancellationToken cancellationToken)
    {
        var chains = await reads.ListEnabledChainsAsync(cancellationToken);
        return CatalogResults.Ok(
            chains.Select(chain => new
            {
                id = chain.Id,
                family = chain.Family,
                name = chain.Name,
                nativeAssetSymbol = chain.NativeAssetSymbol,
                nativeDecimals = chain.NativeDecimals,
                explorerLinkTemplate = chain.ExplorerLinkTemplate,
                requiredConfirmations = chain.RequiredConfirmations,
            }),
            "Chains loaded.",
            httpContext);
    }

    private static object InstrumentSummary(Instrument instrument)
        => new
        {
            id = instrument.Id,
            kind = CatalogText.ToDb(instrument.Kind),
            name = instrument.Name,
            symbol = instrument.Symbol,
            isin = instrument.Isin,
            listingExchange = instrument.ListingExchange,
            countryOfListing = instrument.CountryOfListing,
            quoteCurrency = instrument.QuoteCurrency,
            sector = instrument.Sector,
            shortDescription = instrument.ShortDescription,
            logoLink = instrument.LogoLink,
        };

    private static object? AvailabilityView(TokenAvailability? availability)
        => availability is null
            ? null
            : new
            {
                buyable = availability.Buyable,
                sellable = availability.Sellable,
                buyBlockers = availability.BuyBlockers,
                sellBlockers = availability.SellBlockers,
            };

    private static IResult NotFound(HttpContext httpContext)
        => CatalogResults.Error(new Error(
            ErrorCodes.ResourceNotFound,
            "The catalog resource was not found.",
            ErrorType.NotFound), httpContext);

    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);
}

namespace AskVest.Modules.Catalog.Contracts;

/// <summary>
/// The cross-module view of a catalog instrument.
/// </summary>
public sealed record CatalogInstrument(
    string Id,
    string Kind,
    string Name,
    string Symbol,
    string? Isin,
    string? ListingExchange,
    string CountryOfListing,
    string QuoteCurrency,
    string? Sector,
    string Status,
    string? ShortDescription);

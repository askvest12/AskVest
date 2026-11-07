namespace AskVest.Modules.Catalog.Import.Data;

internal sealed record InstrumentData
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public required string Symbol { get; init; }

    public string? Isin { get; init; }

    public string? ListingExchange { get; init; }

    public required string CountryOfListing { get; init; }

    public required string QuoteCurrency { get; init; }

    public string? Sector { get; init; }

    public string? ShortDescription { get; init; }

    public string? LogoLink { get; init; }

    public required string Status { get; init; }

    public List<TokenData> Tokens { get; init; } = [];
}

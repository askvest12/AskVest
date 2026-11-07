using AskVest.Modules.Catalog.Domain.Enums;

namespace AskVest.Modules.Catalog.Persistence.Entities;

public sealed class Instrument
{
    public required string Id { get; set; }
    public InstrumentKind Kind { get; set; }
    public required string Name { get; set; }
    public required string Symbol { get; set; }
    public string? Isin { get; set; }
    public string? ListingExchange { get; set; }
    public required string CountryOfListing { get; set; }
    public required string QuoteCurrency { get; set; }
    public string? Sector { get; set; }
    public string? ShortDescription { get; set; }
    public string? LogoLink { get; set; }
    public InstrumentStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

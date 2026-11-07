namespace AskVest.Modules.Catalog.Persistence.Entities;

public sealed class TokenCountry
{
    public Guid TokenId { get; set; }
    public required string CountryCode { get; set; }
}

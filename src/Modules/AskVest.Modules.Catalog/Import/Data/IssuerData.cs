namespace AskVest.Modules.Catalog.Import.Data;

internal sealed record IssuerData
{
    public required string Id { get; init; }

    public required string LegalName { get; init; }

    public required string Jurisdiction { get; init; }

    public string? Website { get; init; }

    public string? RegulatoryStatus { get; init; }

    public List<string> LegalDocumentLinks { get; init; } = [];

    public required string Status { get; init; }

    public DateTimeOffset? ReviewedAt { get; init; }

    public DateTimeOffset? ReviewExpiresAt { get; init; }
}

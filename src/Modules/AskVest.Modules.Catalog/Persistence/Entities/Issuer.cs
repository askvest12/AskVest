using AskVest.Modules.Catalog.Domain.Enums;

namespace AskVest.Modules.Catalog.Persistence.Entities;

public sealed class Issuer
{
    public required string Id { get; set; }
    public required string LegalName { get; set; }
    public required string Jurisdiction { get; set; }
    public string? Website { get; set; }
    public string? RegulatoryStatus { get; set; }
    public List<string> LegalDocumentLinks { get; set; } = [];
    public IssuerStatus Status { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? ReviewExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

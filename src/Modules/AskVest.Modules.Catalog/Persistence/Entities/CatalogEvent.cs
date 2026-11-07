using AskVest.Modules.Catalog.Domain.Enums;

namespace AskVest.Modules.Catalog.Persistence.Entities;

public sealed class CatalogEvent
{
    public Guid Id { get; set; }
    public CatalogEventType EventType { get; set; }
    public required string SubjectType { get; set; }
    public required string SubjectId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public List<string> ChangedFields { get; set; } = [];
    public string? DataVersion { get; set; }
    public string? CorrelationId { get; set; }
}

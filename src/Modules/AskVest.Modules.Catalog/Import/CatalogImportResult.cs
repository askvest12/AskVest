using AskVest.Modules.Catalog.Domain.Enums;
using AskVest.Modules.Catalog.Persistence.Entities;

namespace AskVest.Modules.Catalog.Import;

/// <summary>
/// Everything a validated import will write. The validator mutates the
/// DbContext directly; the service decides whether the work commits.
/// </summary>
internal sealed class CatalogImportResult
{
    public List<string> Problems { get; } = [];

    public List<string> Warnings { get; } = [];

    public List<CatalogEvent> Events { get; } = [];

    public List<(string MessageType, object Payload, string? DedupeKey)> OutboxMessages { get; } = [];

    public List<(string Code, int Scale)> AssetsToRegister { get; } = [];

    public int ChainsUpserted { get; set; }

    public int IssuersUpserted { get; set; }

    public int InstrumentsUpserted { get; set; }

    public int TokensUpserted { get; set; }

    public int TokensDelisted { get; set; }

    public bool IsValid => Problems.Count == 0;
}

using AskVest.Modules.Catalog.Domain.Enums;

namespace AskVest.Modules.Catalog.Persistence.Entities;

public sealed class Chain
{
    public required string Id { get; set; }
    public required string Family { get; set; }
    public required string Name { get; set; }
    public required string NativeAssetSymbol { get; set; }
    public int NativeDecimals { get; set; }
    public required string ExplorerLinkTemplate { get; set; }
    public int RequiredConfirmations { get; set; }
    public ChainStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

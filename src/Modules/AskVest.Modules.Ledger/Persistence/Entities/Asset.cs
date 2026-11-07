using AskVest.Modules.Ledger.Contracts;

namespace AskVest.Modules.Ledger.Persistence.Entities;

public sealed class Asset
{
    public string Code { get; set; } = string.Empty;

    public AssetKind Kind { get; set; }

    public int Scale { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

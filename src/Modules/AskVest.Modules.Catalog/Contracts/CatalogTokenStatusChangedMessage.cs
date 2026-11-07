namespace AskVest.Modules.Catalog.Contracts;

/// <summary>
/// Outbox payload emitted once per token status change. Carries ids only.
/// </summary>
public sealed record CatalogTokenStatusChangedMessage(Guid TokenId, string Status)
{
    public const string MessageType = "CatalogTokenStatusChanged";
}

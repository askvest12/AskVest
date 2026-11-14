using AskVest.Modules.Wallets.Domain;

namespace AskVest.Modules.Wallets.Persistence.Entities;

/// <summary>
/// Append-only wallet audit trail. The application role can insert and read
/// but never update or delete; the migration revokes those rights.
/// </summary>
public sealed class WalletEvent
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string SubjectType { get; set; } = string.Empty;

    public Guid SubjectId { get; set; }

    public WalletEventType EventType { get; set; }

    public string? FromState { get; set; }

    public string? ToState { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string? CorrelationId { get; set; }

    public string? DetailCode { get; set; }
}

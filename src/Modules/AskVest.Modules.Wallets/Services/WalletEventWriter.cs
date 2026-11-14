using AskVest.Modules.Wallets.Domain;
using AskVest.Modules.Wallets.Persistence;
using AskVest.Modules.Wallets.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Wallets.Services;

/// <summary>
/// Appends to the restricted wallet_events table. Events carry ids, states
/// and codes only: never amounts, addresses, memos or grant tokens.
/// </summary>
internal sealed class WalletEventWriter(WalletsDbContext db, TimeProvider timeProvider)
{
    public void Write(
        Guid userId,
        WalletEventType eventType,
        string subjectType,
        Guid subjectId,
        string? fromState,
        string? toState,
        string? correlationId,
        string? detailCode = null)
    {
        db.Events.Add(new WalletEvent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            SubjectType = subjectType,
            SubjectId = subjectId,
            EventType = eventType,
            FromState = fromState,
            ToState = toState,
            OccurredAt = timeProvider.GetUtcNow(),
            CorrelationId = correlationId,
            DetailCode = detailCode,
        });
    }
}

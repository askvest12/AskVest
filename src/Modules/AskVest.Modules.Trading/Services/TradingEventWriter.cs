using AskVest.Modules.Trading.Domain;
using AskVest.Modules.Trading.Persistence;
using AskVest.Modules.Trading.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Trading.Services;

/// <summary>
/// Appends to the restricted trading_events table. Events carry ids, states
/// and codes only: never amounts, prices or grant tokens.
/// </summary>
internal sealed class TradingEventWriter(TradingDbContext db, TimeProvider timeProvider)
{
    public void Write(
        Guid userId,
        TradingEventType eventType,
        Guid? orderId,
        OrderState? fromState,
        OrderState? toState,
        string? correlationId,
        string? detailCode = null)
    {
        db.Events.Add(new TradingEvent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            OrderId = orderId,
            EventType = eventType,
            FromState = fromState is { } from ? TradingText.ToDb(from) : null,
            ToState = toState is { } to ? TradingText.ToDb(to) : null,
            OccurredAt = timeProvider.GetUtcNow(),
            CorrelationId = correlationId,
            DetailCode = detailCode,
        });
    }
}

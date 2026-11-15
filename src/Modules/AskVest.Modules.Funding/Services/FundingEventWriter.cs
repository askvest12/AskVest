using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Persistence;
using AskVest.Modules.Funding.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// Appends to the append-only funding_events table. Events carry ids,
/// states and codes only: never amounts, account details, provider payloads
/// or grant tokens.
/// </summary>
internal sealed class FundingEventWriter(FundingDbContext db, TimeProvider timeProvider)
{
    public void Write(
        Guid userId,
        FundingEventType eventType,
        string subjectType,
        Guid subjectId,
        string? fromState,
        string? toState,
        string? correlationId,
        string? detailCode = null)
    {
        db.Events.Add(new FundingEvent
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

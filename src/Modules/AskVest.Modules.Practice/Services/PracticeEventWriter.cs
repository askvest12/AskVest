using AskVest.Modules.Practice.Domain;
using AskVest.Modules.Practice.Persistence.Entities;

namespace AskVest.Modules.Practice.Services;

internal sealed class PracticeEventWriter(
    Persistence.PracticeDbContext db,
    TimeProvider timeProvider)
{
    public void Write(
        Guid userId,
        PracticeEventType eventType,
        Guid subjectId,
        string? correlationId = null)
    {
        db.PracticeEvents.Add(new PracticeEvent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            EventType = eventType,
            SubjectId = subjectId,
            OccurredAt = timeProvider.GetUtcNow(),
            CorrelationId = correlationId,
        });
    }
}

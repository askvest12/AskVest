using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Identity.Services;

public sealed class SecurityEventRecorder(IdentityDbContext db, TimeProvider timeProvider)
{
    public SecurityEvent Record(
        SecurityEventType eventType,
        Guid? userId = null,
        string? ip = null,
        string? userAgent = null,
        string? correlationId = null,
        Guid? deviceId = null,
        string? metadata = null)
    {
        var securityEvent = new SecurityEvent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            EventType = eventType,
            OccurredAt = timeProvider.GetUtcNow(),
            Ip = ip,
            UserAgent = userAgent,
            CorrelationId = correlationId,
            DeviceId = deviceId,
            Metadata = metadata,
        };
        db.SecurityEvents.Add(securityEvent);
        return securityEvent;
    }
}

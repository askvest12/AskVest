using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence;
using AskVest.Modules.Compliance.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Compliance.Services;

/// <summary>
/// Loads or creates the per-user compliance profile and appends compliance
/// events. Profiles are created lazily on first write.
/// </summary>
internal sealed class ProfileStore(ComplianceDbContext db, TimeProvider timeProvider)
{
    public async Task<ComplianceProfile> GetOrCreateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profile = await db.ComplianceProfiles.FindAsync([userId], cancellationToken);
        if (profile is not null)
        {
            return profile;
        }

        var now = timeProvider.GetUtcNow();
        profile = new ComplianceProfile
        {
            UserId = userId,
            KycStatus = KycStatus.NotStarted,
            ScreeningStatus = ScreeningStatus.NotRun,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ComplianceProfiles.Add(profile);
        return profile;
    }

    public async Task<ComplianceProfile?> FindAsync(Guid userId, CancellationToken cancellationToken)
        => await db.ComplianceProfiles.FindAsync([userId], cancellationToken);

    public void AddEvent(Guid? userId, ComplianceEventType eventType, string? correlationId, string? metadata = null)
    {
        db.ComplianceEvents.Add(new ComplianceEvent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            EventType = eventType,
            OccurredAt = timeProvider.GetUtcNow(),
            CorrelationId = correlationId,
            Metadata = metadata is null ? null : System.Text.Json.JsonSerializer.Serialize(metadata),
        });
    }
}

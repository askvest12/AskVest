using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Identity.Persistence.Configurations;

public sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("security_events", table => table.HasCheckConstraint(
            "ck_security_events_event_type",
            $"event_type IN ({string.Join(", ", Enum.GetNames<SecurityEventType>().Select(n => $"'{n}'"))})"));
        builder.HasKey(securityEvent => securityEvent.Id);
        builder.HasIndex(securityEvent => new { securityEvent.UserId, securityEvent.OccurredAt });
        builder.Property(securityEvent => securityEvent.EventType).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(securityEvent => securityEvent.Ip).HasMaxLength(45);
        builder.Property(securityEvent => securityEvent.UserAgent).HasMaxLength(500);
        builder.Property(securityEvent => securityEvent.CorrelationId).HasMaxLength(64);
        builder.Property(securityEvent => securityEvent.Metadata).HasColumnType("jsonb");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(securityEvent => securityEvent.UserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Device>().WithMany().HasForeignKey(securityEvent => securityEvent.DeviceId).OnDelete(DeleteBehavior.SetNull);
    }
}

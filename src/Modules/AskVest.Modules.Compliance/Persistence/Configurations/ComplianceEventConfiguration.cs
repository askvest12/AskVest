using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Compliance.Persistence.Configurations;

public sealed class ComplianceEventConfiguration : IEntityTypeConfiguration<ComplianceEvent>
{
    public void Configure(EntityTypeBuilder<ComplianceEvent> builder)
    {
        builder.ToTable("compliance_events", table => table.HasCheckConstraint(
            "ck_compliance_events_event_type",
            $"event_type IN ({string.Join(", ", Enum.GetNames<ComplianceEventType>().Select(n => $"'{n}'"))})"));
        builder.HasKey(complianceEvent => complianceEvent.Id);
        builder.HasIndex(complianceEvent => new { complianceEvent.UserId, complianceEvent.OccurredAt });
        builder.Property(complianceEvent => complianceEvent.EventType).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(complianceEvent => complianceEvent.OccurredAt).IsRequired();
        builder.Property(complianceEvent => complianceEvent.CorrelationId).HasMaxLength(64);
        builder.Property(complianceEvent => complianceEvent.Metadata).HasColumnType("jsonb");
    }
}

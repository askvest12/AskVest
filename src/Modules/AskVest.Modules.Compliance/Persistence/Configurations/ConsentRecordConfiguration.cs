using AskVest.Modules.Compliance.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Compliance.Persistence.Configurations;

public sealed class ConsentRecordConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> builder)
    {
        builder.ToTable("consent_records");
        builder.HasKey(record => record.Id);
        builder.HasIndex(record => new { record.UserId, record.DocumentType, record.Version }).IsUnique();
        builder.Property(record => record.DocumentType).HasMaxLength(32).IsRequired();
        builder.Property(record => record.Version).HasMaxLength(32).IsRequired();
        builder.Property(record => record.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(record => record.AcceptedAt).IsRequired();
        builder.Property(record => record.Ip).HasMaxLength(45);
        builder.Property(record => record.UserAgent).HasMaxLength(500);
    }
}

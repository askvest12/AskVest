using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Identity.Persistence.Configurations;

public sealed class ThrottleBucketConfiguration : IEntityTypeConfiguration<ThrottleBucket>
{
    public void Configure(EntityTypeBuilder<ThrottleBucket> builder)
    {
        builder.ToTable("throttle_buckets", table => table.HasCheckConstraint(
            "ck_throttle_buckets_kind",
            $"kind IN ({string.Join(", ", Enum.GetNames<ThrottleKind>().Select(n => $"'{n}'"))})"));
        builder.HasKey(bucket => bucket.Id);
        builder.Property(bucket => bucket.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(bucket => bucket.KeyHash).HasColumnType("char(64)").IsRequired();
        builder.HasIndex(bucket => new { bucket.Kind, bucket.KeyHash }).IsUnique();
        builder.HasIndex(bucket => bucket.LockedUntil);
        builder.Property(e => e.Xmin).IsRowVersion();
    }
}

using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Identity.Persistence.Configurations;

public sealed class OneTimeCodeConfiguration : IEntityTypeConfiguration<OneTimeCode>
{
    public void Configure(EntityTypeBuilder<OneTimeCode> builder)
    {
        builder.ToTable("one_time_codes", table =>
        {
            table.HasCheckConstraint(
                "ck_one_time_codes_purpose",
                $"purpose IN ({string.Join(", ", Enum.GetNames<OneTimeCodePurpose>().Select(n => $"'{n}'"))})");
            table.HasCheckConstraint(
                "ck_one_time_codes_destination_type",
                $"destination_type IN ({string.Join(", ", Enum.GetNames<CodeDestinationType>().Select(n => $"'{n}'"))})");
        });
        builder.HasKey(code => code.Id);
        builder.HasIndex(code => new { code.UserId, code.Purpose });
        builder.HasIndex(code => code.ExpiresAt);
        builder.Property(code => code.Purpose).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(code => code.DestinationType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(code => code.Destination).HasMaxLength(320).IsRequired();
        builder.Property(code => code.CodeHash).HasColumnType("char(64)").IsRequired();
        builder.Property(code => code.AttemptsAllowed).IsRequired();
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(code => code.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Device>().WithMany().HasForeignKey(code => code.DeviceId).OnDelete(DeleteBehavior.SetNull);
        builder.Property(e => e.Xmin).IsRowVersion();
    }
}

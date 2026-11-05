using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Identity.Persistence.Configurations;

public sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.ToTable("users", table => table.HasCheckConstraint(
            "ck_users_status",
            $"status IN ({string.Join(", ", Enum.GetNames<UserStatus>().Select(n => $"'{n}'"))})"));
        builder.HasKey(user => user.Id);
        builder.Property(user => user.NormalizedEmail).HasMaxLength(254).IsRequired();
        builder.HasIndex(user => user.NormalizedEmail).IsUnique();
        builder.Property(user => user.PasswordHash).HasColumnType("text").IsRequired();
        builder.Property(user => user.PhoneE164).HasMaxLength(20);
        builder.HasIndex(user => user.PhoneE164)
            .IsUnique()
            .HasFilter("phone_verified_at IS NOT NULL");
        builder.Property(user => user.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(user => user.PinHash).HasColumnType("text");
        builder.Property(user => user.CreatedAt).IsRequired();
        builder.Property(user => user.UpdatedAt).IsRequired();
        builder.Property(e => e.Xmin).IsRowVersion();
    }
}

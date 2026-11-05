using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Identity.Persistence.Configurations;

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_roles");
        builder.HasKey(role => new { role.UserId, role.RoleName });
        builder.Property(role => role.RoleName).HasMaxLength(64).IsRequired();
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(role => role.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

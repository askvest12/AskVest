using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Identity.Persistence.Configurations;

public sealed class StepUpGrantConfiguration : IEntityTypeConfiguration<StepUpGrant>
{
    public void Configure(EntityTypeBuilder<StepUpGrant> builder)
    {
        builder.ToTable("step_up_grants");
        builder.HasKey(grant => grant.Id);
        builder.HasIndex(grant => grant.UserId);
        builder.HasIndex(grant => grant.SessionId);
        builder.HasIndex(grant => grant.ExpiresAt);
        builder.Property(grant => grant.Purpose).HasMaxLength(64).IsRequired();
        builder.Property(grant => grant.BindingHash).HasColumnType("char(64)");
        builder.Property(grant => grant.TokenHash).HasColumnType("char(64)").IsRequired();
        builder.HasIndex(grant => grant.TokenHash).IsUnique();
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(grant => grant.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<UserSession>().WithMany().HasForeignKey(grant => grant.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(e => e.Xmin).IsRowVersion();
    }
}

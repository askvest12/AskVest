using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Identity.Persistence.Configurations;

public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("sessions");
        builder.HasKey(session => session.Id);
        builder.HasIndex(session => session.UserId);
        builder.HasIndex(session => session.DeviceId);
        builder.HasIndex(session => session.AbsoluteExpiresAt);
        builder.Property(session => session.RevokedReason).HasMaxLength(64);
        builder.Property(session => session.CreationIp).HasMaxLength(45);
        builder.Property(session => session.UserAgent).HasMaxLength(500);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Device>().WithMany().HasForeignKey(session => session.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(e => e.Xmin).IsRowVersion();
    }
}

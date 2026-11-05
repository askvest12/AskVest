using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Identity.Persistence.Configurations;

public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("devices");
        builder.HasKey(device => device.Id);
        builder.Property(device => device.InstallationId).HasMaxLength(128).IsRequired();
        builder.HasIndex(device => new { device.UserId, device.InstallationId }).IsUnique();
        builder.HasIndex(device => device.UserId);
        builder.Property(device => device.Platform).HasMaxLength(32).IsRequired();
        builder.Property(device => device.DeviceName).HasMaxLength(200).IsRequired();
        builder.Property(device => device.AppVersion).HasMaxLength(50);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(device => device.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(e => e.Xmin).IsRowVersion();
    }
}

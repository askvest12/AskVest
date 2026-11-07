using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Compliance.Persistence.Configurations;

public sealed class ComplianceProfileConfiguration : IEntityTypeConfiguration<ComplianceProfile>
{
    public void Configure(EntityTypeBuilder<ComplianceProfile> builder)
    {
        builder.ToTable("compliance_profiles", table => table.HasCheckConstraint(
            "ck_compliance_profiles_kyc_status",
            $"kyc_status IN ({string.Join(", ", Enum.GetNames<KycStatus>().Select(n => $"'{n}'"))})"));
        builder.ToTable("compliance_profiles", table => table.HasCheckConstraint(
            "ck_compliance_profiles_screening_status",
            $"screening_status IN ({string.Join(", ", Enum.GetNames<ScreeningStatus>().Select(n => $"'{n}'"))})"));
        builder.HasKey(profile => profile.UserId);
        builder.Property(profile => profile.DeclaredCountry).HasMaxLength(2);
        builder.Property(profile => profile.KycStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(profile => profile.KycProvider).HasMaxLength(64);
        builder.Property(profile => profile.ProviderReference).HasMaxLength(128);
        builder.Property(profile => profile.VerifiedCountry).HasMaxLength(2);
        builder.Property(profile => profile.AttemptsUsed).IsRequired();
        builder.Property(profile => profile.RejectionCode).HasMaxLength(64);
        builder.Property(profile => profile.ScreeningStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(profile => profile.CreatedAt).IsRequired();
        builder.Property(profile => profile.UpdatedAt).IsRequired();
        builder.Property(profile => profile.Xmin).IsRowVersion();
    }
}

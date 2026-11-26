using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Identity.Persistence.Configurations;

public sealed class OnboardingProfileConfiguration : IEntityTypeConfiguration<OnboardingProfile>
{
    public void Configure(EntityTypeBuilder<OnboardingProfile> builder)
    {
        builder.ToTable("onboarding_profiles", table => table.HasCheckConstraint(
            "ck_onboarding_profiles_familiarity",
            $"investing_familiarity IS NULL OR investing_familiarity IN ({string.Join(", ", Enum.GetNames<OnboardingFamiliarity>().Select(n => $"'{n}'"))})"));
        builder.HasKey(profile => profile.UserId);
        builder.Property(profile => profile.InvestingFamiliarity).HasConversion<string>().HasMaxLength(32);
        builder.Property(profile => profile.GoalFlags).IsRequired();
        builder.Property(profile => profile.PreferredCurrency).HasMaxLength(3);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(profile => profile.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(profile => profile.CreatedAt).IsRequired();
        builder.Property(profile => profile.UpdatedAt).IsRequired();
        builder.Property(e => e.Xmin).IsRowVersion();
    }
}

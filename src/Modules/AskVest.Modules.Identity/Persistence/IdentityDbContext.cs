using AskVest.Modules.Identity.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Identity.Persistence;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string Schema = "identity";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OneTimeCode> OneTimeCodes => Set<OneTimeCode>();
    public DbSet<ThrottleBucket> ThrottleBuckets => Set<ThrottleBucket>();
    public DbSet<StepUpGrant> StepUpGrants => Set<StepUpGrant>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);
    }
}

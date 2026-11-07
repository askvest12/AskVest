using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Compliance.Persistence;

public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options) : DbContext(options)
{
    public const string Schema = "compliance";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<Entities.ConsentRecord> ConsentRecords => Set<Entities.ConsentRecord>();
    public DbSet<Entities.ComplianceProfile> ComplianceProfiles => Set<Entities.ComplianceProfile>();
    public DbSet<Entities.KycWebhookEvent> KycWebhookEvents => Set<Entities.KycWebhookEvent>();
    public DbSet<Entities.ComplianceEvent> ComplianceEvents => Set<Entities.ComplianceEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ComplianceDbContext).Assembly);
    }
}

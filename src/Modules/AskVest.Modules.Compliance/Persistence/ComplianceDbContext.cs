using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Compliance.Persistence;

public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options) : DbContext(options)
{
    public const string Schema = "compliance";
    public const string HistoryTable = "__ef_migrations_history";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ComplianceDbContext).Assembly);
    }
}

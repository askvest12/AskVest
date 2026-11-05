using Microsoft.EntityFrameworkCore;

namespace AskVest.Platform;

public class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string Schema = "platform";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("idempotency_keys");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Scope).HasMaxLength(128).IsRequired();
            entity.Property(record => record.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(record => record.RequestMethod).HasMaxLength(10).IsRequired();
            entity.Property(record => record.RequestPath).HasMaxLength(512).IsRequired();
            entity.Property(record => record.RequestFingerprint).HasColumnType("char(64)").IsRequired();
            entity.Property(record => record.State).HasMaxLength(16).IsRequired();
            entity.Property(record => record.ResponseContentType).HasMaxLength(100);
            entity.Property(record => record.ResponseBody).HasColumnType("text");
            entity.HasIndex(record => new { record.Scope, record.IdempotencyKey }).IsUnique();
            entity.HasIndex(record => record.ExpiresAt);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_idempotency_keys_state",
                $"state IN ('{IdempotencyStates.InProgress}', '{IdempotencyStates.Completed}')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_idempotency_keys_completed_has_status",
                "state <> 'completed' OR response_status_code IS NOT NULL"));
        });
    }
}

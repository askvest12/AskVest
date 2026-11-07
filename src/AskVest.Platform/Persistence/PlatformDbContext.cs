using Microsoft.EntityFrameworkCore;

namespace AskVest.Platform;

public class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string Schema = "platform";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

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

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.MessageType).HasColumnName("type").HasMaxLength(128).IsRequired();
            entity.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(message => message.Status).HasMaxLength(16).IsRequired();
            entity.Property(message => message.Outcome).HasMaxLength(32);
            entity.Property(message => message.LastError).HasMaxLength(256);
            entity.Property(message => message.DedupeKey).HasMaxLength(200);
            entity.HasIndex(message => message.DedupeKey).IsUnique();
            entity.HasIndex(message => new { message.Status, message.AvailableAt });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_outbox_messages_status",
                $"status IN ('{OutboxMessage.StatusPending}', '{OutboxMessage.StatusProcessed}', '{OutboxMessage.StatusDead}')"));
        });
    }
}

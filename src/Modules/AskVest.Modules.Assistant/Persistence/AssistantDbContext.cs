using AskVest.Modules.Assistant.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Assistant.Persistence;

public class AssistantDbContext(DbContextOptions<AssistantDbContext> options) : DbContext(options)
{
    public const string Schema = "assistant";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("conversations");
            entity.HasKey(conversation => conversation.Id);
            entity.Property(conversation => conversation.Title).HasMaxLength(60).IsRequired();
            entity.HasIndex(conversation => new { conversation.UserId, conversation.UpdatedAt });
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.ToTable("messages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Role).HasMaxLength(16).IsRequired();
            entity.Property(message => message.Text).IsRequired();
            entity.Property(message => message.Citations).HasColumnType("jsonb").IsRequired();
            entity.Property(message => message.FactCards).HasColumnType("jsonb").IsRequired();
            entity.Property(message => message.Actions).HasColumnType("jsonb").IsRequired();
            entity.Property(message => message.PolicyOutcome).HasMaxLength(32);
            entity.Property(message => message.IdempotencyKey).HasMaxLength(128);
            entity.HasIndex(message => new { message.ConversationId, message.CreatedAt });
            entity.HasIndex(message => new { message.UserId, message.CreatedAt });
            entity.HasIndex(message => new { message.UserId, message.IdempotencyKey })
                .IsUnique()
                .HasFilter("\"idempotency_key\" IS NOT NULL");
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.ModelId).HasMaxLength(64).IsRequired();
            entity.Property(audit => audit.PolicyVersion).HasMaxLength(64).IsRequired();
            entity.Property(audit => audit.Outcome).HasMaxLength(32).IsRequired();
            entity.Property(audit => audit.ToolNames).HasColumnType("jsonb").IsRequired();
            entity.Property(audit => audit.CitationSourceIds).HasColumnType("jsonb").IsRequired();
            entity.Property(audit => audit.ToolResultHashes).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(audit => new { audit.UserId, audit.CreatedAt });
        });
    }
}

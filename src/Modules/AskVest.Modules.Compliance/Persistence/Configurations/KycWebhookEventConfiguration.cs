using AskVest.Modules.Compliance.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AskVest.Modules.Compliance.Persistence.Configurations;

public sealed class KycWebhookEventConfiguration : IEntityTypeConfiguration<KycWebhookEvent>
{
    public void Configure(EntityTypeBuilder<KycWebhookEvent> builder)
    {
        builder.ToTable("kyc_webhook_events");
        builder.HasKey(webhookEvent => webhookEvent.Id);
        builder.HasIndex(webhookEvent => new { webhookEvent.Provider, webhookEvent.EventId }).IsUnique();
        builder.Property(webhookEvent => webhookEvent.Provider).HasMaxLength(64).IsRequired();
        builder.Property(webhookEvent => webhookEvent.EventId).HasMaxLength(128).IsRequired();
        builder.Property(webhookEvent => webhookEvent.ReceivedAt).IsRequired();
        builder.Property(webhookEvent => webhookEvent.Outcome).HasMaxLength(32).IsRequired();
        builder.Property(webhookEvent => webhookEvent.BodyHash).HasMaxLength(64).IsRequired();
    }
}

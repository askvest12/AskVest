using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Funding.Persistence;

public class FundingDbContext(DbContextOptions<FundingDbContext> options) : DbContext(options)
{
    public const string Schema = "funding";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<FundingAccount> FundingAccounts => Set<FundingAccount>();

    public DbSet<FundingDeposit> Deposits => Set<FundingDeposit>();

    public DbSet<FundingConversion> Conversions => Set<FundingConversion>();

    public DbSet<FundingBeneficiary> Beneficiaries => Set<FundingBeneficiary>();

    public DbSet<FundingWithdrawal> Withdrawals => Set<FundingWithdrawal>();

    public DbSet<FundingWebhookEvent> WebhookEvents => Set<FundingWebhookEvent>();

    public DbSet<FundingRestriction> Restrictions => Set<FundingRestriction>();

    public DbSet<FundingPause> Pauses => Set<FundingPause>();

    public DbSet<FundingEvent> Events => Set<FundingEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<FundingAccount>(entity =>
        {
            entity.ToTable("funding_accounts");
            entity.HasKey(account => account.Id);
            entity.Property(account => account.UserId).IsRequired();
            entity.Property(account => account.Currency).HasMaxLength(3).IsRequired();
            entity.Property(account => account.ProviderKey).HasMaxLength(32).IsRequired();
            entity.Property(account => account.ProviderReference).HasMaxLength(128).IsRequired();
            entity.Property(account => account.InstructionJson).HasColumnType("text").IsRequired();
            entity.Property(account => account.CreatedAt).IsRequired();
            entity.HasIndex(account => new { account.UserId, account.Currency }).IsUnique();
            entity.HasIndex(account => account.ProviderReference).IsUnique();
        });

        modelBuilder.Entity<FundingDeposit>(entity =>
        {
            entity.ToTable("deposits");
            entity.HasKey(deposit => deposit.Id);
            entity.Property(deposit => deposit.ProviderKey).HasMaxLength(32).IsRequired();
            entity.Property(deposit => deposit.ProviderDepositId).HasMaxLength(128).IsRequired();
            entity.Property(deposit => deposit.UserId).IsRequired();
            entity.Property(deposit => deposit.Currency).HasMaxLength(3).IsRequired();
            entity.Property(deposit => deposit.Amount).HasColumnType("numeric(28,8)").IsRequired();
            entity.Property(deposit => deposit.ProviderFee).HasColumnType("numeric(28,8)").IsRequired();
            entity.Property(deposit => deposit.PayerVerified).IsRequired();
            entity.Property(deposit => deposit.State)
                .HasConversion(v => FundingText.ToDb(v), v => FundingText.DepositStateFromDb(v))
                .HasMaxLength(16).IsRequired();
            entity.Property(deposit => deposit.DetailCode).HasMaxLength(64);
            entity.Property(deposit => deposit.Shortfall).HasColumnType("numeric(28,8)").IsRequired();
            entity.Property(deposit => deposit.CreatedAt).IsRequired();
            entity.Property(deposit => deposit.UpdatedAt).IsRequired();
            entity.HasIndex(deposit => new { deposit.ProviderKey, deposit.ProviderDepositId }).IsUnique();
            entity.HasIndex(deposit => new { deposit.UserId, deposit.CreatedAt });
            entity.HasIndex(deposit => new { deposit.State, deposit.CreatedAt });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_deposits_state",
                "state IN ('pending', 'credited', 'quarantined', 'failed', 'reversed', 'ignored')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_deposits_amount",
                "amount > 0 AND provider_fee >= 0 AND shortfall >= 0"));
        });

        modelBuilder.Entity<FundingConversion>(entity =>
        {
            entity.ToTable("conversions");
            entity.HasKey(conversion => conversion.Id);
            entity.Property(conversion => conversion.UserId).IsRequired();
            entity.Property(conversion => conversion.Pair).HasMaxLength(7).IsRequired();
            entity.Property(conversion => conversion.SourceCurrency).HasMaxLength(3).IsRequired();
            entity.Property(conversion => conversion.TargetCurrency).HasMaxLength(3).IsRequired();
            entity.Property(conversion => conversion.SourceAmount).HasColumnType("numeric(28,8)").IsRequired();
            entity.Property(conversion => conversion.MidRate).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(conversion => conversion.EffectiveRate).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(conversion => conversion.ReceiveAmount).HasColumnType("numeric(28,8)").IsRequired();
            entity.Property(conversion => conversion.FeeAmount).HasColumnType("numeric(28,8)").IsRequired();
            entity.Property(conversion => conversion.NetAmount).HasColumnType("numeric(28,8)").IsRequired();
            entity.Property(conversion => conversion.TermsHash).HasMaxLength(64).IsRequired();
            entity.Property(conversion => conversion.State)
                .HasConversion(v => FundingText.ToDb(v), v => FundingText.ConversionStateFromDb(v))
                .HasMaxLength(16).IsRequired();
            entity.Property(conversion => conversion.ExpiresAt).IsRequired();
            entity.Property(conversion => conversion.IdempotencyKey).HasMaxLength(128);
            entity.Property(conversion => conversion.CreatedAt).IsRequired();
            entity.HasIndex(conversion => new { conversion.UserId, conversion.State });
            entity.HasIndex(conversion => new { conversion.UserId, conversion.IdempotencyKey }).IsUnique()
                .HasFilter("idempotency_key IS NOT NULL");
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_conversions_state",
                "state IN ('quoted', 'completed', 'expired')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_conversions_amounts",
                "source_amount > 0 AND receive_amount >= 0 AND fee_amount >= 0 AND net_amount >= 0"));
        });

        modelBuilder.Entity<FundingBeneficiary>(entity =>
        {
            entity.ToTable("beneficiaries");
            entity.HasKey(beneficiary => beneficiary.Id);
            entity.Property(beneficiary => beneficiary.UserId).IsRequired();
            entity.Property(beneficiary => beneficiary.Currency).HasMaxLength(3).IsRequired();
            entity.Property(beneficiary => beneficiary.ProviderKey).HasMaxLength(32).IsRequired();
            entity.Property(beneficiary => beneficiary.ProviderReference).HasMaxLength(128).IsRequired();
            entity.Property(beneficiary => beneficiary.MaskedDisplay).HasMaxLength(128).IsRequired();
            entity.Property(beneficiary => beneficiary.NameVerified).IsRequired();
            entity.Property(beneficiary => beneficiary.State)
                .HasConversion(v => FundingText.ToDb(v), v => FundingText.BeneficiaryStateFromDb(v))
                .HasMaxLength(16).IsRequired();
            entity.Property(beneficiary => beneficiary.CreatedAt).IsRequired();
            entity.HasIndex(beneficiary => new { beneficiary.UserId, beneficiary.Currency, beneficiary.State });
            entity.HasIndex(beneficiary => new { beneficiary.UserId, beneficiary.ProviderKey, beneficiary.ProviderReference })
                .IsUnique();
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_beneficiaries_state",
                "state IN ('cooling_off', 'active', 'removed')"));
        });

        modelBuilder.Entity<FundingWithdrawal>(entity =>
        {
            entity.ToTable("withdrawals");
            entity.HasKey(withdrawal => withdrawal.Id);
            entity.Property(withdrawal => withdrawal.UserId).IsRequired();
            entity.Property(withdrawal => withdrawal.Currency).HasMaxLength(3).IsRequired();
            entity.Property(withdrawal => withdrawal.BeneficiaryId).IsRequired();
            entity.Property(withdrawal => withdrawal.ReceiveAmount).HasColumnType("numeric(28,8)").IsRequired();
            entity.Property(withdrawal => withdrawal.FeeAmount).HasColumnType("numeric(28,8)").IsRequired();
            entity.Property(withdrawal => withdrawal.TotalDebited).HasColumnType("numeric(28,8)").IsRequired();
            entity.Property(withdrawal => withdrawal.TermsHash).HasMaxLength(64).IsRequired();
            entity.Property(withdrawal => withdrawal.State)
                .HasConversion(v => FundingText.ToDb(v), v => FundingText.WithdrawalStateFromDb(v))
                .HasMaxLength(24).IsRequired();
            entity.Property(withdrawal => withdrawal.ExpiresAt).IsRequired();
            entity.Property(withdrawal => withdrawal.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(withdrawal => withdrawal.ProviderReference).HasMaxLength(128);
            entity.Property(withdrawal => withdrawal.DetailCode).HasMaxLength(64);
            entity.Property(withdrawal => withdrawal.ReviewDecision).HasMaxLength(32);
            entity.Property(withdrawal => withdrawal.CreatedAt).IsRequired();
            entity.Property(withdrawal => withdrawal.UpdatedAt).IsRequired();
            entity.Property(withdrawal => withdrawal.Xmin).IsRowVersion();
            entity.HasIndex(withdrawal => new { withdrawal.UserId, withdrawal.IdempotencyKey }).IsUnique();
            entity.HasIndex(withdrawal => new { withdrawal.UserId, withdrawal.State });
            entity.HasIndex(withdrawal => new { withdrawal.State, withdrawal.UpdatedAt });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_withdrawals_state",
                "state IN ('previewed', 'requested', 'under_review', 'submitting', 'submitted', 'completed', 'failed', 'cancelled', 'needs_reconciliation')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_withdrawals_amounts",
                "receive_amount > 0 AND fee_amount >= 0 AND total_debited > 0"));
        });

        modelBuilder.Entity<FundingWebhookEvent>(entity =>
        {
            entity.ToTable("webhook_events");
            entity.HasKey(webhookEvent => webhookEvent.Id);
            entity.Property(webhookEvent => webhookEvent.ProviderKey).HasMaxLength(32).IsRequired();
            entity.Property(webhookEvent => webhookEvent.EventId).HasMaxLength(128).IsRequired();
            entity.Property(webhookEvent => webhookEvent.ReceivedAt).IsRequired();
            entity.Property(webhookEvent => webhookEvent.Outcome).HasMaxLength(32).IsRequired();
            entity.Property(webhookEvent => webhookEvent.BodyHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(webhookEvent => new { webhookEvent.ProviderKey, webhookEvent.EventId }).IsUnique();
        });

        modelBuilder.Entity<FundingRestriction>(entity =>
        {
            entity.ToTable("funding_restrictions");
            entity.HasKey(restriction => restriction.Id);
            entity.Property(restriction => restriction.UserId).IsRequired();
            entity.Property(restriction => restriction.ReasonCode).HasMaxLength(64).IsRequired();
            entity.Property(restriction => restriction.Since).IsRequired();
            entity.HasIndex(restriction => new { restriction.UserId, restriction.ClearedAt });
        });

        modelBuilder.Entity<FundingPause>(entity =>
        {
            entity.ToTable("funding_pauses");
            entity.HasKey(pause => pause.Id);
            entity.Property(pause => pause.Currency).HasMaxLength(3).IsRequired();
            entity.Property(pause => pause.ReasonCode).HasMaxLength(64).IsRequired();
            entity.Property(pause => pause.ConsecutiveShortfalls).IsRequired();
            entity.Property(pause => pause.CreatedAt).IsRequired();
            entity.Property(pause => pause.UpdatedAt).IsRequired();
            entity.HasIndex(pause => pause.Currency).IsUnique();
        });

        modelBuilder.Entity<FundingEvent>(entity =>
        {
            entity.ToTable("funding_events");
            entity.HasKey(fundingEvent => fundingEvent.Id);
            entity.Property(fundingEvent => fundingEvent.UserId).IsRequired();
            entity.Property(fundingEvent => fundingEvent.SubjectType).HasMaxLength(32).IsRequired();
            entity.Property(fundingEvent => fundingEvent.EventType)
                .HasConversion(v => FundingText.ToDb(v), v => FundingText.EventTypeFromDb(v))
                .HasMaxLength(32).IsRequired();
            entity.Property(fundingEvent => fundingEvent.FromState).HasMaxLength(24);
            entity.Property(fundingEvent => fundingEvent.ToState).HasMaxLength(24);
            entity.Property(fundingEvent => fundingEvent.OccurredAt).IsRequired();
            entity.Property(fundingEvent => fundingEvent.CorrelationId).HasMaxLength(64);
            entity.Property(fundingEvent => fundingEvent.DetailCode).HasMaxLength(64);
            entity.HasIndex(fundingEvent => new { fundingEvent.UserId, fundingEvent.OccurredAt });
            entity.HasIndex(fundingEvent => fundingEvent.SubjectId);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_funding_events_type",
                "event_type IN ('funding_account_created', 'deposit_state_changed', 'conversion_completed', 'beneficiary_state_changed', 'withdrawal_state_changed', 'restriction_added', 'restriction_cleared', 'pause_changed', 'operator_action')"));
        });
    }
}

using AskVest.Modules.Wallets.Domain;
using AskVest.Modules.Wallets.Persistence.Entities;
using AskVest.Modules.Wallets.Services;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Wallets.Persistence;

public class WalletsDbContext(DbContextOptions<WalletsDbContext> options) : DbContext(options)
{
    public const string Schema = "wallets";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<WalletAddress> WalletAddresses => Set<WalletAddress>();

    public DbSet<Deposit> Deposits => Set<Deposit>();

    public DbSet<WithdrawalAddress> WithdrawalAddresses => Set<WithdrawalAddress>();

    public DbSet<Withdrawal> Withdrawals => Set<Withdrawal>();

    public DbSet<CustodyPause> CustodyPauses => Set<CustodyPause>();

    public DbSet<WalletEvent> Events => Set<WalletEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<WalletAddress>(entity =>
        {
            entity.ToTable("wallet_addresses");
            entity.HasKey(address => address.Id);
            entity.Property(address => address.ChainId).HasMaxLength(64).IsRequired();
            entity.Property(address => address.Address).HasMaxLength(128).IsRequired();
            entity.Property(address => address.Memo).HasMaxLength(256).IsRequired();
            entity.Property(address => address.ProviderReference).HasMaxLength(128).IsRequired();
            entity.Property(address => address.CreatedAt).IsRequired();
            entity.HasIndex(address => new { address.ChainId, address.Address, address.Memo }).IsUnique();
            entity.HasIndex(address => new { address.UserId, address.ChainId });
        });

        modelBuilder.Entity<Deposit>(entity =>
        {
            entity.ToTable("deposits");
            entity.HasKey(deposit => deposit.Id);
            entity.Property(deposit => deposit.ProviderKey).HasMaxLength(32).IsRequired();
            entity.Property(deposit => deposit.ProviderDepositId).HasMaxLength(128).IsRequired();
            entity.Property(deposit => deposit.ChainId).HasMaxLength(64).IsRequired();
            entity.Property(deposit => deposit.AssetCode).HasMaxLength(16).IsRequired();
            entity.Property(deposit => deposit.Quantity).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(deposit => deposit.SenderAddress).HasMaxLength(128).IsRequired();
            entity.Property(deposit => deposit.SettlementReference).HasMaxLength(128);
            entity.Property(deposit => deposit.State)
                .HasConversion(v => WalletsText.ToDb(v), v => WalletsText.DepositStateFromDb(v))
                .HasMaxLength(16).IsRequired();
            entity.Property(deposit => deposit.DetailCode).HasMaxLength(64);
            entity.Property(deposit => deposit.DetectedAt).IsRequired();
            entity.Property(deposit => deposit.CreditedAt);
            entity.HasIndex(deposit => new { deposit.ProviderKey, deposit.ProviderDepositId }).IsUnique();
            entity.HasIndex(deposit => new { deposit.UserId, deposit.DetectedAt });
            entity.HasIndex(deposit => new { deposit.ChainId, deposit.State });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_deposits_state",
                "state IN ('pending', 'credited', 'quarantined', 'ignored')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_deposits_quantity",
                "quantity > 0"));
        });

        modelBuilder.Entity<WithdrawalAddress>(entity =>
        {
            entity.ToTable("withdrawal_addresses");
            entity.HasKey(address => address.Id);
            entity.Property(address => address.ChainId).HasMaxLength(64).IsRequired();
            entity.Property(address => address.CanonicalAddress).HasMaxLength(128).IsRequired();
            entity.Property(address => address.Label).HasMaxLength(64).IsRequired();
            entity.Property(address => address.State)
                .HasConversion(v => WalletsText.ToDb(v), v => WalletsText.WithdrawalAddressStateFromDb(v))
                .HasMaxLength(16).IsRequired();
            entity.Property(address => address.ScreeningResult).HasMaxLength(16);
            entity.Property(address => address.ScreeningReference).HasMaxLength(128);
            entity.Property(address => address.CreatedAt).IsRequired();
            entity.Property(address => address.ActiveFrom);
            entity.Property(address => address.RemovedAt);
            entity.HasIndex(address => new { address.UserId, address.State });
            entity.HasIndex(address => new { address.UserId, address.ChainId, address.CanonicalAddress });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_withdrawal_addresses_state",
                "state IN ('cooling_off', 'active', 'removed')"));
        });

        modelBuilder.Entity<Withdrawal>(entity =>
        {
            entity.ToTable("withdrawals");
            entity.HasKey(withdrawal => withdrawal.Id);
            entity.Property(withdrawal => withdrawal.DestinationChainId).HasMaxLength(64).IsRequired();
            entity.Property(withdrawal => withdrawal.DestinationAddress).HasMaxLength(128).IsRequired();
            entity.Property(withdrawal => withdrawal.GrossQuantity).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(withdrawal => withdrawal.Fee).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(withdrawal => withdrawal.NetQuantity).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(withdrawal => withdrawal.TermsHash).HasMaxLength(64).IsRequired();
            entity.Property(withdrawal => withdrawal.State)
                .HasConversion(v => WalletsText.ToDb(v), v => WalletsText.WithdrawalStateFromDb(v))
                .HasMaxLength(24).IsRequired();
            entity.Property(withdrawal => withdrawal.ExpiresAt).IsRequired();
            entity.Property(withdrawal => withdrawal.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(withdrawal => withdrawal.ProviderReference).HasMaxLength(128);
            entity.Property(withdrawal => withdrawal.SettlementReference).HasMaxLength(128);
            entity.Property(withdrawal => withdrawal.DetailCode).HasMaxLength(64);
            entity.Property(withdrawal => withdrawal.CreatedAt).IsRequired();
            entity.Property(withdrawal => withdrawal.UpdatedAt).IsRequired();
            entity.Property(withdrawal => withdrawal.Xmin).IsRowVersion();
            entity.HasIndex(withdrawal => new { withdrawal.UserId, withdrawal.IdempotencyKey }).IsUnique();
            entity.HasIndex(withdrawal => new { withdrawal.UserId, withdrawal.State });
            entity.HasIndex(withdrawal => new { withdrawal.State, withdrawal.UpdatedAt });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_withdrawals_state",
                "state IN ('previewed', 'requested', 'submitting', 'submitted', 'completed', 'failed', 'cancelled', 'needs_reconciliation')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_withdrawals_amounts",
                "gross_quantity > 0 AND fee >= 0 AND net_quantity > 0"));
        });

        modelBuilder.Entity<CustodyPause>(entity =>
        {
            entity.ToTable("custody_pauses");
            entity.HasKey(pause => pause.Id);
            entity.Property(pause => pause.ChainId).HasMaxLength(64).IsRequired();
            entity.Property(pause => pause.ReasonCode).HasMaxLength(64).IsRequired();
            entity.Property(pause => pause.ConsecutiveMismatches).IsRequired();
            entity.Property(pause => pause.CreatedAt).IsRequired();
            entity.Property(pause => pause.UpdatedAt).IsRequired();
            entity.HasIndex(pause => new { pause.ChainId, pause.TokenId });
        });

        modelBuilder.Entity<WalletEvent>(entity =>
        {
            entity.ToTable("wallet_events");
            entity.HasKey(walletEvent => walletEvent.Id);
            entity.Property(walletEvent => walletEvent.SubjectType).HasMaxLength(32).IsRequired();
            entity.Property(walletEvent => walletEvent.EventType)
                .HasConversion(v => WalletsText.ToDb(v), v => WalletsText.EventTypeFromDb(v))
                .HasMaxLength(32).IsRequired();
            entity.Property(walletEvent => walletEvent.FromState).HasMaxLength(24);
            entity.Property(walletEvent => walletEvent.ToState).HasMaxLength(24);
            entity.Property(walletEvent => walletEvent.OccurredAt).IsRequired();
            entity.Property(walletEvent => walletEvent.CorrelationId).HasMaxLength(64);
            entity.Property(walletEvent => walletEvent.DetailCode).HasMaxLength(64);
            entity.HasIndex(walletEvent => new { walletEvent.UserId, walletEvent.OccurredAt });
            entity.HasIndex(walletEvent => walletEvent.SubjectId);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_wallet_events_type",
                "event_type IN ('deposit_credited', 'withdrawal_address_added', 'withdrawal_state_changed')"));
        });
    }
}

using AskVest.Modules.Practice.Domain;
using AskVest.Modules.Practice.Persistence.Entities;
using AskVest.Modules.Practice.Services;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Practice.Persistence;

public class PracticeDbContext(DbContextOptions<PracticeDbContext> options) : DbContext(options)
{
    public const string Schema = "practice";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<PracticeAccount> PracticeAccounts => Set<PracticeAccount>();

    public DbSet<PracticeInstrument> PracticeInstruments => Set<PracticeInstrument>();

    public DbSet<PracticeOrder> PracticeOrders => Set<PracticeOrder>();

    public DbSet<PracticeEvent> PracticeEvents => Set<PracticeEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<PracticeAccount>(entity =>
        {
            entity.ToTable("practice_accounts");
            entity.HasKey(account => account.UserId);
            entity.Property(account => account.StartingCreditsUsed).HasPrecision(38, 18).IsRequired();
            entity.Property(account => account.ResetCount).IsRequired();
            entity.Property(account => account.StartedAt).IsRequired();
            entity.Property(account => account.UpdatedAt).IsRequired();
            entity.Property(account => account.Xmin).IsRowVersion();
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_practice_accounts_reset_count",
                "reset_count >= 0"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_practice_accounts_starting_credits",
                "starting_credits_used >= 0"));
        });

        modelBuilder.Entity<PracticeInstrument>(entity =>
        {
            entity.ToTable("practice_instruments");
            entity.HasKey(instrument => instrument.InstrumentId);
            entity.Property(instrument => instrument.InstrumentId).HasMaxLength(64).IsRequired();
            entity.Property(instrument => instrument.AssetCode).HasMaxLength(16).IsRequired();
            entity.Property(instrument => instrument.CreatedAt).IsRequired();
            entity.HasIndex(instrument => instrument.AssetCode).IsUnique();
        });

        modelBuilder.Entity<PracticeOrder>(entity =>
        {
            entity.ToTable("practice_orders");
            entity.HasKey(order => order.Id);
            entity.Property(order => order.InstrumentId).HasMaxLength(64).IsRequired();
            entity.Property(order => order.Side)
                .HasConversion(v => PracticeText.ToDb(v), v => PracticeText.SideFromDb(v))
                .HasMaxLength(8).IsRequired();
            entity.Property(order => order.Type)
                .HasConversion(v => PracticeText.ToDb(v), v => PracticeText.TypeFromDb(v))
                .HasMaxLength(12).IsRequired();
            entity.Property(order => order.State)
                .HasConversion(v => PracticeText.ToDb(v), v => PracticeText.StateFromDb(v))
                .HasMaxLength(12).IsRequired();
            entity.Property(order => order.RequestedAmount).HasPrecision(38, 18);
            entity.Property(order => order.RequestedQuantity).HasPrecision(38, 18);
            entity.Property(order => order.StopPrice).HasPrecision(38, 18);
            entity.Property(order => order.FilledQuantity).HasPrecision(38, 18);
            entity.Property(order => order.FillPrice).HasPrecision(38, 18);
            entity.Property(order => order.CashAmount).HasPrecision(38, 18);
            entity.Property(order => order.Fee).HasPrecision(38, 18);
            entity.Property(order => order.PriceLabel).HasMaxLength(16);
            entity.Property(order => order.PriceSession).HasMaxLength(16);
            entity.Property(order => order.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(order => order.CancelReason).HasMaxLength(64);
            entity.Property(order => order.CreatedAt).IsRequired();
            entity.Property(order => order.Xmin).IsRowVersion();
            entity.HasIndex(order => new { order.UserId, order.IdempotencyKey }).IsUnique();
            entity.HasIndex(order => new { order.UserId, order.State });
            entity.HasIndex(order => new { order.State, order.Type });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_practice_orders_side",
                "side IN ('buy', 'sell')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_practice_orders_type",
                "type IN ('market', 'stop_loss')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_practice_orders_state",
                "state IN ('open', 'filled', 'cancelled')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_practice_orders_amounts",
                "(requested_amount IS NULL OR requested_amount > 0)"
                + " AND (requested_quantity IS NULL OR requested_quantity > 0)"
                + " AND (stop_price IS NULL OR stop_price > 0)"
                + " AND (filled_quantity IS NULL OR filled_quantity > 0)"
                + " AND (fill_price IS NULL OR fill_price > 0)"
                + " AND (cash_amount IS NULL OR cash_amount >= 0)"
                + " AND (fee IS NULL OR fee >= 0)"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_practice_orders_stop_shape",
                "(type = 'market') = (stop_price IS NULL)"));
        });

        modelBuilder.Entity<PracticeEvent>(entity =>
        {
            entity.ToTable("practice_events");
            entity.HasKey(practiceEvent => practiceEvent.Id);
            entity.Property(practiceEvent => practiceEvent.EventType)
                .HasConversion(v => PracticeText.ToDb(v), v => PracticeText.EventTypeFromDb(v))
                .HasMaxLength(20).IsRequired();
            entity.Property(practiceEvent => practiceEvent.SubjectId).IsRequired();
            entity.Property(practiceEvent => practiceEvent.OccurredAt).IsRequired();
            entity.Property(practiceEvent => practiceEvent.CorrelationId).HasMaxLength(64);
            entity.HasIndex(practiceEvent => new { practiceEvent.UserId, practiceEvent.OccurredAt });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_practice_events_type",
                "event_type IN ('account_started', 'account_reset', 'order_filled', 'order_cancelled', 'stop_triggered')"));
        });
    }
}

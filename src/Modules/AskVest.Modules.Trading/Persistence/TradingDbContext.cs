using AskVest.Modules.Trading.Domain;
using AskVest.Modules.Trading.Persistence.Entities;
using AskVest.Modules.Trading.Services;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Trading.Persistence;

public class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public const string Schema = "trading";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<TradingQuote> Quotes => Set<TradingQuote>();

    public DbSet<TradingOrder> Orders => Set<TradingOrder>();

    public DbSet<TradingEvent> Events => Set<TradingEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<TradingQuote>(entity =>
        {
            entity.ToTable("quotes");
            entity.HasKey(quote => quote.Id);
            entity.Property(quote => quote.Side)
                .HasConversion(v => TradingText.ToDb(v), v => TradingText.SideFromDb(v))
                .HasMaxLength(8).IsRequired();
            entity.Property(quote => quote.State)
                .HasConversion(v => TradingText.ToDb(v), v => TradingText.QuoteStateFromDb(v))
                .HasMaxLength(8).IsRequired();
            entity.Property(quote => quote.Quantity).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(quote => quote.Price).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(quote => quote.GrossAmount).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(quote => quote.OurFee).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(quote => quote.GatewayFee).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(quote => quote.Total).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(quote => quote.SettlementAsset).HasMaxLength(16).IsRequired();
            entity.Property(quote => quote.GatewayKey).HasMaxLength(64).IsRequired();
            entity.Property(quote => quote.GatewayQuoteId).HasMaxLength(128).IsRequired();
            entity.Property(quote => quote.Warnings).HasMaxLength(512).IsRequired();
            entity.Property(quote => quote.TermsHash).HasMaxLength(64).IsRequired();
            entity.Property(quote => quote.ExpiresAt).IsRequired();
            entity.Property(quote => quote.CreatedAt).IsRequired();
            entity.HasIndex(quote => new { quote.UserId, quote.CreatedAt });
            entity.HasIndex(quote => new { quote.UserId, quote.State });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_quotes_state",
                "state IN ('open', 'used')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_quotes_amounts",
                "quantity > 0 AND price > 0 AND gross_amount >= 0"
                + " AND our_fee >= 0 AND gateway_fee >= 0 AND total > 0"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_quotes_side",
                "side IN ('buy', 'sell')"));
        });

        modelBuilder.Entity<TradingOrder>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(order => order.Id);
            entity.Property(order => order.InstrumentId).HasMaxLength(64).IsRequired();
            entity.Property(order => order.Side)
                .HasConversion(v => TradingText.ToDb(v), v => TradingText.SideFromDb(v))
                .HasMaxLength(8).IsRequired();
            entity.Property(order => order.State)
                .HasConversion(v => TradingText.ToDb(v), v => TradingText.StateFromDb(v))
                .HasMaxLength(24).IsRequired();
            entity.Property(order => order.SettlementAsset).HasMaxLength(16).IsRequired();
            entity.Property(order => order.Quantity).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(order => order.Price).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(order => order.GrossAmount).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(order => order.OurFee).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(order => order.GatewayFee).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(order => order.Total).HasColumnType("numeric(28,18)").IsRequired();
            entity.Property(order => order.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(order => order.GatewayKey).HasMaxLength(64).IsRequired();
            entity.Property(order => order.GatewayOrderReference).HasMaxLength(128);
            entity.Property(order => order.SettlementChainId).HasMaxLength(64);
            entity.Property(order => order.SettlementReference).HasMaxLength(128);
            entity.Property(order => order.FailureCode).HasMaxLength(64);
            entity.Property(order => order.DetailCode).HasMaxLength(64);
            entity.Property(order => order.CreatedAt).IsRequired();
            entity.Property(order => order.UpdatedAt).IsRequired();
            entity.Property(order => order.Xmin).IsRowVersion();
            entity.HasIndex(order => order.QuoteId).IsUnique();
            entity.HasIndex(order => new { order.UserId, order.IdempotencyKey }).IsUnique();
            entity.HasIndex(order => new { order.UserId, order.State });
            entity.HasIndex(order => new { order.State, order.UpdatedAt });
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_orders_state",
                "state IN ('pending_submission', 'submitting', 'submitted', 'executed', 'failed', 'cancelled', 'needs_reconciliation')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_orders_side",
                "side IN ('buy', 'sell')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_orders_amounts",
                "quantity > 0 AND price > 0 AND gross_amount > 0"
                + " AND our_fee >= 0 AND gateway_fee >= 0 AND total > 0"));
        });

        modelBuilder.Entity<TradingEvent>(entity =>
        {
            entity.ToTable("trading_events");
            entity.HasKey(tradingEvent => tradingEvent.Id);
            entity.Property(tradingEvent => tradingEvent.EventType)
                .HasConversion(v => TradingText.ToDb(v), v => TradingText.EventTypeFromDb(v))
                .HasMaxLength(32).IsRequired();
            entity.Property(tradingEvent => tradingEvent.FromState).HasMaxLength(24);
            entity.Property(tradingEvent => tradingEvent.ToState).HasMaxLength(24);
            entity.Property(tradingEvent => tradingEvent.OccurredAt).IsRequired();
            entity.Property(tradingEvent => tradingEvent.CorrelationId).HasMaxLength(64);
            entity.Property(tradingEvent => tradingEvent.DetailCode).HasMaxLength(64);
            entity.HasIndex(tradingEvent => new { tradingEvent.UserId, tradingEvent.OccurredAt });
            entity.HasIndex(tradingEvent => tradingEvent.OrderId);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_trading_events_type",
                "event_type IN ('order_placed', 'order_submitted', 'order_executed', 'order_failed', 'order_cancelled', 'order_needs_reconciliation', 'order_reconciliation_resolved')"));
        });
    }
}

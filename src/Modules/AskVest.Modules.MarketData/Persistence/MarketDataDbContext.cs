using AskVest.Modules.MarketData.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.MarketData.Persistence;

public class MarketDataDbContext(DbContextOptions<MarketDataDbContext> options) : DbContext(options)
{
    public const string Schema = "marketdata";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<LatestPrice> LatestPrices => Set<LatestPrice>();
    public DbSet<Candle> Candles => Set<Candle>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<LatestPrice>(entity =>
        {
            entity.ToTable("latest_prices");
            entity.HasKey(price => new { price.SubjectType, price.SubjectId });
            entity.Property(price => price.SubjectType).HasMaxLength(16).IsRequired();
            entity.Property(price => price.SubjectId).HasMaxLength(64).IsRequired();
            entity.Property(price => price.Price).HasPrecision(38, 18).IsRequired();
            entity.Property(price => price.Currency).HasMaxLength(8).IsRequired();
            entity.Property(price => price.Session).HasMaxLength(16).IsRequired();
            entity.Property(price => price.PreviousClose).HasPrecision(38, 18);
            entity.Property(price => price.ProviderKey).HasMaxLength(50).IsRequired();
            entity.Property(price => price.SuspectPrice).HasPrecision(38, 18);
            entity.HasIndex(price => price.AsOf);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_latest_prices_subject_type",
                "subject_type = 'instrument'"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_latest_prices_price",
                "price > 0"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_latest_prices_previous_close",
                "previous_close IS NULL OR previous_close > 0"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_latest_prices_suspect_price",
                "suspect_price IS NULL OR suspect_price > 0"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_latest_prices_session",
                "session IN ('pre', 'regular', 'post', 'closed', 'halted', 'unknown')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_latest_prices_delay",
                "delay_minutes >= 0"));
        });

        modelBuilder.Entity<Candle>(entity =>
        {
            entity.ToTable("candles");
            entity.HasKey(candle => new { candle.SubjectType, candle.SubjectId, candle.Interval, candle.OpenTime });
            entity.Property(candle => candle.SubjectType).HasMaxLength(16).IsRequired();
            entity.Property(candle => candle.SubjectId).HasMaxLength(64).IsRequired();
            entity.Property(candle => candle.Interval).HasMaxLength(8).IsRequired();
            entity.Property(candle => candle.Open).HasPrecision(38, 18).IsRequired();
            entity.Property(candle => candle.High).HasPrecision(38, 18).IsRequired();
            entity.Property(candle => candle.Low).HasPrecision(38, 18).IsRequired();
            entity.Property(candle => candle.Close).HasPrecision(38, 18).IsRequired();
            entity.Property(candle => candle.Volume).HasPrecision(38, 18).IsRequired();
            entity.Property(candle => candle.ProviderKey).HasMaxLength(50).IsRequired();
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_candles_subject_type",
                "subject_type = 'instrument'"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_candles_interval",
                "interval IN ('15m', '1h', '1d')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_candles_ohlc",
                "open > 0 AND high > 0 AND low > 0 AND close > 0"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_candles_high_low",
                "high >= low"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_candles_volume",
                "volume >= 0"));
        });
    }
}

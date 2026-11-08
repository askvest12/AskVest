using AskVest.Modules.Portfolio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Portfolio.Persistence;

public class PortfolioDbContext(DbContextOptions<PortfolioDbContext> options) : DbContext(options)
{
    public const string Schema = "portfolio";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<WatchlistItem>(entity =>
        {
            entity.ToTable("watchlist_items");
            entity.HasKey(item => new { item.UserId, item.InstrumentId });
            entity.Property(item => item.InstrumentId).HasMaxLength(64).IsRequired();
            entity.Property(item => item.AddedAt).IsRequired();
            entity.HasIndex(item => new { item.UserId, item.AddedAt });
        });
    }
}

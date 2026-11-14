using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Wallets.Persistence;

public class WalletsDbContext(DbContextOptions<WalletsDbContext> options) : DbContext(options)
{
    public const string Schema = "wallets";
    public const string HistoryTable = "__ef_migrations_history";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
    }
}

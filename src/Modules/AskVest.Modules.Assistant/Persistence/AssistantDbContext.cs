using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Assistant.Persistence;

public class AssistantDbContext(DbContextOptions<AssistantDbContext> options) : DbContext(options)
{
    public const string Schema = "assistant";
    public const string HistoryTable = "__ef_migrations_history";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
    }
}

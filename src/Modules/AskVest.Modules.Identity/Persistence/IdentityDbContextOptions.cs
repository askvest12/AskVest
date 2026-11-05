using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Identity.Persistence;

public static class IdentityDbContextOptions
{
    public static DbContextOptions<IdentityDbContext> Create(string connectionString, int commandTimeoutSeconds = 30)
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        Configure(builder, connectionString, commandTimeoutSeconds);
        return builder.Options;
    }

    public static void Configure(DbContextOptionsBuilder builder, string connectionString, int commandTimeoutSeconds)
    {
        builder.UseNpgsql(
                connectionString,
                npgsql =>
                {
                    npgsql.CommandTimeout(commandTimeoutSeconds);
                    npgsql.MigrationsHistoryTable(IdentityDbContext.HistoryTable, IdentityDbContext.Schema);
                })
            .UseSnakeCaseNamingConvention();
    }
}

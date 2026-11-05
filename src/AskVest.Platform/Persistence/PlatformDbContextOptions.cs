using Microsoft.EntityFrameworkCore;

namespace AskVest.Platform;

public static class PlatformDbContextOptions
{
    public static DbContextOptions<PlatformDbContext> Create(string connectionString, int commandTimeoutSeconds = 30)
    {
        var builder = new DbContextOptionsBuilder<PlatformDbContext>();
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
                    npgsql.MigrationsHistoryTable(PlatformDbContext.HistoryTable, PlatformDbContext.Schema);
                })
            .UseSnakeCaseNamingConvention();
    }
}

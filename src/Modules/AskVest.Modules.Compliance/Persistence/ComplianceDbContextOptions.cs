using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Compliance.Persistence;

public static class ComplianceDbContextOptions
{
    public static DbContextOptions<ComplianceDbContext> Create(string connectionString, int commandTimeoutSeconds = 30)
    {
        var builder = new DbContextOptionsBuilder<ComplianceDbContext>();
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
                    npgsql.MigrationsHistoryTable(ComplianceDbContext.HistoryTable, ComplianceDbContext.Schema);
                })
            .UseSnakeCaseNamingConvention();
    }
}

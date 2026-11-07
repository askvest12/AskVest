using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Ledger.Persistence;

public static class LedgerDbContextOptions
{
    public static DbContextOptions<LedgerDbContext> Create(string connectionString, int commandTimeoutSeconds = 30)
    {
        var builder = new DbContextOptionsBuilder<LedgerDbContext>();
        Configure(builder, connectionString, commandTimeoutSeconds);
        return builder.Options;
    }

    public static void Configure(
        DbContextOptionsBuilder builder,
        string connectionString,
        int commandTimeoutSeconds,
        DbConnection? sharedConnection = null)
    {
        void NpgsqlOptions(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql)
        {
            npgsql.CommandTimeout(commandTimeoutSeconds);
            npgsql.MigrationsHistoryTable(LedgerDbContext.HistoryTable, LedgerDbContext.Schema);
        }

        if (sharedConnection is null)
        {
            builder.UseNpgsql(connectionString, NpgsqlOptions);
        }
        else
        {
            builder.UseNpgsql(sharedConnection, NpgsqlOptions);
        }

        builder.UseSnakeCaseNamingConvention();
    }
}

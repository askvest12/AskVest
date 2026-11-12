using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Trading.Persistence;

public static class TradingDbContextOptions
{
    public static DbContextOptions<TradingDbContext> Create(string connectionString, int commandTimeoutSeconds = 30)
    {
        var builder = new DbContextOptionsBuilder<TradingDbContext>();
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
            npgsql.MigrationsHistoryTable(TradingDbContext.HistoryTable, TradingDbContext.Schema);
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

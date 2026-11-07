using System.Data.Common;
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

    public static void Configure(
        DbContextOptionsBuilder builder,
        string connectionString,
        int commandTimeoutSeconds,
        DbConnection? sharedConnection = null)
    {
        void NpgsqlOptions(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql)
        {
            npgsql.CommandTimeout(commandTimeoutSeconds);
            npgsql.MigrationsHistoryTable(IdentityDbContext.HistoryTable, IdentityDbContext.Schema);
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

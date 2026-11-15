using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Funding.Persistence;

public static class FundingDbContextOptions
{
    public static DbContextOptions<FundingDbContext> Create(string connectionString, int commandTimeoutSeconds = 30)
    {
        var builder = new DbContextOptionsBuilder<FundingDbContext>();
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
            npgsql.MigrationsHistoryTable(FundingDbContext.HistoryTable, FundingDbContext.Schema);
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

using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Learning.Persistence;

public static class LearningDbContextOptions
{
    public static DbContextOptions<LearningDbContext> Create(string connectionString, int commandTimeoutSeconds = 30)
    {
        var builder = new DbContextOptionsBuilder<LearningDbContext>();
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
            npgsql.MigrationsHistoryTable(LearningDbContext.HistoryTable, LearningDbContext.Schema);
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

using Microsoft.EntityFrameworkCore;

namespace AskVest.Platform;

public static class MigrationRunner
{
    private static readonly IReadOnlyList<Func<string, DbContext>> ContextFactories =
    [
        connectionString => new PlatformDbContext(PlatformDbContextOptions.Create(connectionString)),
    ];

    public static async Task<IReadOnlyList<string>> RunAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var applied = new List<string>();
        foreach (var factory in ContextFactories)
        {
            await using var context = factory(connectionString);
            var pending = (await context.Database
                .GetPendingMigrationsAsync(cancellationToken)).ToList();
            await context.Database.MigrateAsync(cancellationToken);
            applied.AddRange(pending);
        }

        return applied;
    }
}

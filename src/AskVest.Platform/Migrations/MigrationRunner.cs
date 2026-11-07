using Microsoft.EntityFrameworkCore;

namespace AskVest.Platform;

public static class MigrationRunner
{
    private static readonly IReadOnlyList<Func<string, DbContext>> _contextFactories =
    [
        connectionString => new PlatformDbContext(PlatformDbContextOptions.Create(connectionString)),
    ];

    public static async Task<IReadOnlyList<string>> RunAsync(
        string connectionString,
        IReadOnlyList<Func<string, DbContext>>? additionalContextFactories = null,
        CancellationToken cancellationToken = default)
    {
        var applied = new List<string>();
        var factories = additionalContextFactories is null
            ? _contextFactories
            : _contextFactories.Concat(additionalContextFactories).ToList();
        foreach (var factory in factories)
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

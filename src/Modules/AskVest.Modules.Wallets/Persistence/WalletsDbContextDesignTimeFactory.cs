using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AskVest.Modules.Wallets.Persistence;

public sealed class WalletsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<WalletsDbContext>
{
    public WalletsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ASKVEST_MIGRATOR_CONNECTION")
            ?? throw new InvalidOperationException("ASKVEST_MIGRATOR_CONNECTION is required to scaffold migrations.");

        return new WalletsDbContext(WalletsDbContextOptions.Create(connectionString));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AskVest.Modules.Ledger.Persistence;

public sealed class LedgerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ASKVEST_MIGRATOR_CONNECTION")
            ?? throw new InvalidOperationException("ASKVEST_MIGRATOR_CONNECTION is required to scaffold migrations.");

        return new LedgerDbContext(LedgerDbContextOptions.Create(connectionString));
    }
}

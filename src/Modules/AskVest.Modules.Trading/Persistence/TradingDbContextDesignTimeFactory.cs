using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AskVest.Modules.Trading.Persistence;

public sealed class TradingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ASKVEST_MIGRATOR_CONNECTION")
            ?? throw new InvalidOperationException("ASKVEST_MIGRATOR_CONNECTION is required to scaffold migrations.");

        return new TradingDbContext(TradingDbContextOptions.Create(connectionString));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AskVest.Modules.Funding.Persistence;

public sealed class FundingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<FundingDbContext>
{
    public FundingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ASKVEST_MIGRATOR_CONNECTION")
            ?? throw new InvalidOperationException("ASKVEST_MIGRATOR_CONNECTION is required to scaffold migrations.");

        return new FundingDbContext(FundingDbContextOptions.Create(connectionString));
    }
}

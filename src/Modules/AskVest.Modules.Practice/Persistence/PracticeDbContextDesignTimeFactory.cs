using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AskVest.Modules.Practice.Persistence;

public sealed class PracticeDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PracticeDbContext>
{
    public PracticeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ASKVEST_MIGRATOR_CONNECTION")
            ?? throw new InvalidOperationException("ASKVEST_MIGRATOR_CONNECTION is required to scaffold migrations.");

        return new PracticeDbContext(PracticeDbContextOptions.Create(connectionString));
    }
}

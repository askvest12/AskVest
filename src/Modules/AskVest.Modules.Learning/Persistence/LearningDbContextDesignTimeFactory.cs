using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AskVest.Modules.Learning.Persistence;

public sealed class LearningDbContextDesignTimeFactory : IDesignTimeDbContextFactory<LearningDbContext>
{
    public LearningDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ASKVEST_MIGRATOR_CONNECTION")
            ?? throw new InvalidOperationException("ASKVEST_MIGRATOR_CONNECTION is required to scaffold migrations.");

        return new LearningDbContext(LearningDbContextOptions.Create(connectionString));
    }
}

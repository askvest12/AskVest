using AskVest.IntegrationTests.Infrastructure;
using AskVest.Platform;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class MigrationRunnerTests
{
    private readonly PostgresFixture _fixture;

    public MigrationRunnerTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunningAgain_AppliesNothing()
    {
        var applied = await MigrationRunner.RunAsync(_fixture.OwnerConnectionString, TestContext.Current.CancellationToken);
        applied.ShouldBeEmpty();
    }
}

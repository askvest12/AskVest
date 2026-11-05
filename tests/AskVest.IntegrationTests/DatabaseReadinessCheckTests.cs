using AskVest.IntegrationTests.Infrastructure;
using AskVest.Platform;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class DatabaseReadinessCheckTests
{
    private readonly PostgresFixture _fixture;

    public DatabaseReadinessCheckTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HealthCheckResult> CheckAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var context = new PlatformDbContext(PlatformDbContextOptions.Create(connectionString));
        return await new DatabaseReadinessCheck(context).CheckHealthAsync(new HealthCheckContext(), cancellationToken);
    }

    [Fact]
    public async Task MigratedDatabase_IsHealthy()
    {
        var result = await CheckAsync(_fixture.AppConnectionString, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task PendingMigrations_AreUnhealthy()
    {
        const string databaseName = "askvest_readiness_probe";
        await Database.ExecuteAsync(
            _fixture.AdminConnectionString,
            $"DROP DATABASE IF EXISTS {databaseName}",
            TestContext.Current.CancellationToken);
        await Database.ExecuteAsync(
            _fixture.AdminConnectionString,
            $"CREATE DATABASE {databaseName}",
            TestContext.Current.CancellationToken);
        var probe = new NpgsqlConnectionStringBuilder(_fixture.OwnerConnectionString) { Database = databaseName };
        try
        {
            var result = await CheckAsync(probe.ConnectionString, TestContext.Current.CancellationToken);
            result.Status.ShouldBe(HealthStatus.Unhealthy);
        }
        finally
        {
            // The health check pools a connection to the probe database; release it before dropping.
            await using (var pooled = new NpgsqlConnection(probe.ConnectionString))
            {
                NpgsqlConnection.ClearPool(pooled);
            }

            await Database.ExecuteAsync(
                _fixture.AdminConnectionString,
                $"DROP DATABASE IF EXISTS {databaseName}",
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task UnreachableDatabase_IsUnhealthy()
    {
        var probe = new NpgsqlConnectionStringBuilder(_fixture.OwnerConnectionString)
        {
            Host = "127.0.0.1",
            Port = 1,
            Timeout = 2,
            CommandTimeout = 2,
        };
        var result = await CheckAsync(probe.ConnectionString, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(HealthStatus.Unhealthy);
    }
}

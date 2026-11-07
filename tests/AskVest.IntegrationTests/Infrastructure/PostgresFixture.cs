using AskVest.Modules.Identity.Persistence;
using AskVest.Platform;
using DotNet.Testcontainers.Configurations;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;
using Xunit;

namespace AskVest.IntegrationTests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private const string AdminPassword = "test-admin-password-0123456789abcdef";
    private const string OwnerPassword = "test-owner-password-0123456789abcdef";
    private const string AppPassword = "test-app-password-0123456789abcdef";

    private readonly PostgreSqlContainer _container;
    private Respawner _respawner = null!;

    public PostgresFixture()
    {
        var initDirectory = Path.GetFullPath(Path.Combine(RepositoryRoot(), "docker", "postgres", "init"));
        _container = new PostgreSqlBuilder("postgres:17")
            .WithDatabase("askvest")
            .WithUsername("askvest_admin")
            .WithPassword(AdminPassword)
            .WithEnvironment("ASKVEST_OWNER_PASSWORD", OwnerPassword)
            .WithEnvironment("ASKVEST_APP_PASSWORD", AppPassword)
            .WithBindMount(initDirectory, "/docker-entrypoint-initdb.d", AccessMode.ReadOnly)
            .Build();
    }

    public string OwnerConnectionString { get; private set; } = string.Empty;

    public string AppConnectionString { get; private set; } = string.Empty;

    public string AdminConnectionString => BuildRoleConnectionString("askvest_admin", AdminPassword);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        OwnerConnectionString = BuildRoleConnectionString("askvest_owner", OwnerPassword);
        AppConnectionString = BuildRoleConnectionString("askvest_app", AppPassword);

        var applied = await MigrationRunner.RunAsync(
            OwnerConnectionString,
            [connectionString => new IdentityDbContext(IdentityDbContextOptions.Create(connectionString))]);
        if (applied.Count == 0)
        {
            throw new InvalidOperationException("Expected InitialPlatform to apply on a fresh database.");
        }

        await using (var connection = new NpgsqlConnection(AppConnectionString))
        {
            await connection.OpenAsync();
            _respawner = await Respawner.CreateAsync(
                connection,
                new RespawnerOptions
                {
                    DbAdapter = DbAdapter.Postgres,
                    SchemasToInclude = ["platform", "identity"],
                    TablesToIgnore = ["platform.__ef_migrations_history", "identity.__ef_migrations_history"],
                });
        }
    }

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();
        await _respawner.ResetAsync(connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    private string BuildRoleConnectionString(string username, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = username,
            Password = password,
        };
        return builder.ConnectionString;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AskVest.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}

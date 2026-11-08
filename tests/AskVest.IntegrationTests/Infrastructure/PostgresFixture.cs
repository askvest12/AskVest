using AskVest.Modules.Catalog.Persistence;
using AskVest.Modules.Compliance.Persistence;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Ledger.Persistence;
using AskVest.Modules.MarketData.Persistence;
using AskVest.Modules.Practice.Persistence;
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
    private Respawner _catalogRespawner = null!;

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
            .WithCommand("-c", "max_connections=400")
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
            [
                connectionString => new IdentityDbContext(IdentityDbContextOptions.Create(connectionString)),
                connectionString => new ComplianceDbContext(ComplianceDbContextOptions.Create(connectionString)),
                connectionString => new LedgerDbContext(LedgerDbContextOptions.Create(connectionString)),
                connectionString => new CatalogDbContext(CatalogDbContextOptions.Create(connectionString)),
                connectionString => new MarketDataDbContext(MarketDataDbContextOptions.Create(connectionString)),
                connectionString => new PracticeDbContext(PracticeDbContextOptions.Create(connectionString)),
            ]);
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
                    SchemasToInclude = ["platform", "identity", "compliance", "ledger", "marketdata"],
                    TablesToIgnore = [
                        "platform.__ef_migrations_history",
                        "identity.__ef_migrations_history",
                        "compliance.__ef_migrations_history",
                        "ledger.__ef_migrations_history",
                        "marketdata.__ef_migrations_history",
                    ],
                });
        }

        // The catalog schema is select-only for the app role and practice_events is
        // append-only, so their resets run as the owner while every other schema
        // resets as the app role.
        await using (var ownerConnection = new NpgsqlConnection(OwnerConnectionString))
        {
            await ownerConnection.OpenAsync();
            _catalogRespawner = await Respawner.CreateAsync(
                ownerConnection,
                new RespawnerOptions
                {
                    DbAdapter = DbAdapter.Postgres,
                    SchemasToInclude = ["catalog", "practice"],
                    TablesToIgnore = ["catalog.__ef_migrations_history", "practice.__ef_migrations_history"],
                });
        }
    }

    public async Task ResetAsync()
    {
        await using (var appConnection = new NpgsqlConnection(AppConnectionString))
        {
            await appConnection.OpenAsync();
            await _respawner.ResetAsync(appConnection);
        }

        await using var ownerConnection = new NpgsqlConnection(OwnerConnectionString);
        await ownerConnection.OpenAsync();
        await _catalogRespawner.ResetAsync(ownerConnection);
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

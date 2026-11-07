using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using AskVest.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Platform;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class TransactionScopeTests
{
    private readonly PostgresFixture _fixture;

    public TransactionScopeTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Commit_Persists_Writes_From_Two_Module_Contexts()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var transaction = services.GetRequiredService<ITransactionScope>();
        var identity = services.GetRequiredService<IdentityDbContext>();
        var platform = services.GetRequiredService<PlatformDbContext>();
        var outbox = services.GetRequiredService<IOutbox>();

        await transaction.BeginAsync(TestContext.Current.CancellationToken);
        var bucket = NewBucket();
        identity.ThrottleBuckets.Add(bucket);
        platform.IdempotencyRecords.Add(NewIdempotencyRecord());
        await outbox.EnqueueAsync("test.committed", new { id = Guid.CreateVersion7() }, cancellationToken: TestContext.Current.CancellationToken);
        await identity.SaveChangesAsync(TestContext.Current.CancellationToken);
        await platform.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        (await CountAsync("identity.throttle_buckets", $"id = '{bucket.Id}'")).ShouldBe(1L);
        (await CountAsync("platform.outbox_messages", "type = 'test.committed'")).ShouldBe(1L);
    }

    [Fact]
    public async Task Rollback_Persists_Neither_Context()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var transaction = services.GetRequiredService<ITransactionScope>();
        var identity = services.GetRequiredService<IdentityDbContext>();
        var outbox = services.GetRequiredService<IOutbox>();

        await transaction.BeginAsync(TestContext.Current.CancellationToken);
        var bucket = NewBucket();
        identity.ThrottleBuckets.Add(bucket);
        await outbox.EnqueueAsync("test.rolled_back", new { id = Guid.CreateVersion7() }, cancellationToken: TestContext.Current.CancellationToken);
        await identity.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);

        (await CountAsync("identity.throttle_buckets", $"id = '{bucket.Id}'")).ShouldBe(0L);
        (await CountAsync("platform.outbox_messages", "type = 'test.rolled_back'")).ShouldBe(0L);
    }

    [Fact]
    public async Task Dispose_Without_Commit_Rolls_Back_Everything()
    {
        Guid bucketId;
        await using (var provider = CreateProvider())
        {
            await using var scope = provider.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var transaction = services.GetRequiredService<ITransactionScope>();
            var identity = services.GetRequiredService<IdentityDbContext>();
            var outbox = services.GetRequiredService<IOutbox>();

            await transaction.BeginAsync(TestContext.Current.CancellationToken);
            var bucket = NewBucket();
            bucketId = bucket.Id;
            identity.ThrottleBuckets.Add(bucket);
            await outbox.EnqueueAsync("test.dispose", new { id = bucketId }, cancellationToken: TestContext.Current.CancellationToken);
            await identity.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("identity.throttle_buckets", $"id = '{bucketId}'")).ShouldBe(0L);
        (await CountAsync("platform.outbox_messages", "type = 'test.dispose'")).ShouldBe(0L);
    }

    [Fact]
    public async Task Duplicate_Dedupe_Key_Is_Ignored_Without_Aborting_The_Transaction()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var transaction = services.GetRequiredService<ITransactionScope>();
        var identity = services.GetRequiredService<IdentityDbContext>();
        var outbox = services.GetRequiredService<IOutbox>();

        await transaction.BeginAsync(TestContext.Current.CancellationToken);
        await outbox.EnqueueAsync("test.dedupe", new { n = 1 }, "dedupe-1", TestContext.Current.CancellationToken);
        await outbox.EnqueueAsync("test.dedupe", new { n = 2 }, "dedupe-1", TestContext.Current.CancellationToken);
        var bucket = NewBucket();
        identity.ThrottleBuckets.Add(bucket);
        await identity.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        (await CountAsync("platform.outbox_messages", "dedupe_key = 'dedupe-1'")).ShouldBe(1L);
        (await CountAsync("identity.throttle_buckets", $"id = '{bucket.Id}'")).ShouldBe(1L);
    }

    private static ThrottleBucket NewBucket() => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = ThrottleKind.Account,
        KeyHash = $"{Guid.CreateVersion7():N}{Guid.CreateVersion7():N}",
        FailureCount = 1,
        WindowStart = TimeProvider.System.GetUtcNow(),
    };

    private static IdempotencyRecord NewIdempotencyRecord() => new()
    {
        Id = Guid.CreateVersion7(),
        Scope = "test",
        IdempotencyKey = $"key-{Guid.CreateVersion7():N}",
        RequestMethod = "POST",
        RequestPath = "/api/v1/test",
        RequestFingerprint = new string('a', 64),
        State = IdempotencyStates.InProgress,
        LockedUntil = TimeProvider.System.GetUtcNow().AddMinutes(5),
        CreatedAt = TimeProvider.System.GetUtcNow(),
        ExpiresAt = TimeProvider.System.GetUtcNow().AddHours(1),
    };

    private ServiceProvider CreateProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = _fixture.OwnerConnectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddPlatformPersistence(configuration);
        services.AddDbContext<IdentityDbContext>((provider, options) =>
        {
            var scope = provider.GetService<ITransactionScope>();
            IdentityDbContextOptions.Configure(options, _fixture.OwnerConnectionString, 30, scope?.Connection);
        });
        return services.BuildServiceProvider();
    }

    private async Task<long> CountAsync(string table, string where)
    {
        var rows = await Database.QueryAsync(
            _fixture.OwnerConnectionString,
            $"SELECT count(*) AS n FROM {table} WHERE {where}",
            TestContext.Current.CancellationToken);
        return Convert.ToInt64(rows[0]["n"]);
    }
}

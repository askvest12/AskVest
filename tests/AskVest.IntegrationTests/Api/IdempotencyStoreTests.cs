using AskVest.Api;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Api;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdempotencyStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options =>
            AskVest.Platform.PlatformDbContextOptions.Configure(options, fixture.AppConnectionString, 30));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Begin_Then_Complete_Then_Replay()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        var key = Guid.CreateVersion7().ToString("N");
        var expires = TimeProvider.System.GetUtcNow().AddHours(1);

        var begin = await store.BeginAsync("api", key, "POST", "/x", "fp1", TimeSpan.FromSeconds(60), expires, TestContext.Current.CancellationToken);
        begin.ShouldBe(IdempotencyBeginResult.Acquired);

        var stored = new IdempotencyStoredResponse(200, "application/json", "{\"ok\":true}", false);
        await store.CompleteAsync("api", key, stored, TestContext.Current.CancellationToken);

        var replay = await store.BeginAsync("api", key, "POST", "/x", "fp1", TimeSpan.FromSeconds(60), expires, TestContext.Current.CancellationToken);
        replay.ShouldBe(IdempotencyBeginResult.Replay);
        var completed = await store.GetCompletedAsync("api", key, TestContext.Current.CancellationToken);
        completed.ShouldNotBeNull();
        completed!.Body.ShouldBe("{\"ok\":true}");
    }

    [Fact]
    public async Task Begin_InProgress_Lock_Blocks_Second()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        var key = Guid.CreateVersion7().ToString("N");
        var expires = TimeProvider.System.GetUtcNow().AddHours(1);

        (await store.BeginAsync("api", key, "POST", "/x", "fp", TimeSpan.FromSeconds(60), expires, TestContext.Current.CancellationToken))
            .ShouldBe(IdempotencyBeginResult.Acquired);
        (await store.BeginAsync("api", key, "POST", "/x", "fp", TimeSpan.FromSeconds(60), expires, TestContext.Current.CancellationToken))
            .ShouldBe(IdempotencyBeginResult.InProgress);
    }

    [Fact]
    public async Task Begin_Different_Fingerprint_Returns_KeyReused()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        var key = Guid.CreateVersion7().ToString("N");
        var expires = TimeProvider.System.GetUtcNow().AddHours(1);

        await store.BeginAsync("api", key, "POST", "/x", "fp1", TimeSpan.FromSeconds(60), expires, TestContext.Current.CancellationToken);
        (await store.BeginAsync("api", key, "POST", "/x", "fp2", TimeSpan.FromSeconds(60), expires, TestContext.Current.CancellationToken))
            .ShouldBe(IdempotencyBeginResult.KeyReused);
    }

    [Fact]
    public async Task Release_Removes_Record()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        var key = Guid.CreateVersion7().ToString("N");
        var expires = TimeProvider.System.GetUtcNow().AddHours(1);

        await store.BeginAsync("api", key, "POST", "/x", "fp", TimeSpan.FromSeconds(60), expires, TestContext.Current.CancellationToken);
        await store.ReleaseAsync("api", key, TestContext.Current.CancellationToken);
        (await store.BeginAsync("api", key, "POST", "/x", "fp", TimeSpan.FromSeconds(60), expires, TestContext.Current.CancellationToken))
            .ShouldBe(IdempotencyBeginResult.Acquired);
    }

    [Fact]
    public async Task PurgeExpired_Deletes_Old_Records()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        var key = Guid.CreateVersion7().ToString("N");
        var expired = TimeProvider.System.GetUtcNow().AddHours(-1);

        await store.BeginAsync("api", key, "POST", "/x", "fp", TimeSpan.FromSeconds(60), expired, TestContext.Current.CancellationToken);
        var purged = await store.PurgeExpiredAsync(TimeProvider.System.GetUtcNow(), TestContext.Current.CancellationToken);
        purged.ShouldBeGreaterThanOrEqualTo(1);
    }
}

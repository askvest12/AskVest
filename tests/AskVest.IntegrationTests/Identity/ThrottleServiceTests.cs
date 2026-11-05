using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Services;
using AskVest.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Identity;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class ThrottleServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Lock_Duration_Doubles_On_Repeat_And_Caps_At_Maximum()
    {
        await using var factory = new AuthApiFactory(fixture.AppConnectionString);
        factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var keyedHasher = scope.ServiceProvider.GetRequiredService<IKeyedHasher>();

        var options = Options.Create(new IdentityOptions
        {
            LoginFailuresPerAccount = 3,
            LoginThrottleWindow = TimeSpan.FromDays(1),
            LoginThrottleDuration = TimeSpan.FromMinutes(1),
            LoginThrottleMaxDuration = TimeSpan.FromMinutes(90),
        });
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
        var throttle = new ThrottleService(db, keyedHasher, options, clock);
        var identifier = $"throttle-{Guid.CreateVersion7():N}";

        for (var i = 0; i < 3; i++)
        {
            await throttle.RecordFailureAsync(ThrottleKind.Account, identifier, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var first = await throttle.LockedForAsync(ThrottleKind.Account, identifier, TestContext.Current.CancellationToken);
        first.ShouldNotBeNull();
        first!.Value.TotalMinutes.ShouldBeInRange(0.9, 1.1);

        var expectedMinutes = new[] { 2.0, 4.0, 8.0, 16.0, 32.0, 64.0, 90.0, 90.0 };
        foreach (var expected in expectedMinutes)
        {
            clock.Advance(TimeSpan.FromMinutes(expected + 1));
            (await throttle.LockedForAsync(ThrottleKind.Account, identifier, TestContext.Current.CancellationToken))
                .ShouldBeNull("lock should have expired before the next failure");

            await throttle.RecordFailureAsync(ThrottleKind.Account, identifier, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var lockedFor = await throttle.LockedForAsync(ThrottleKind.Account, identifier, TestContext.Current.CancellationToken);
            lockedFor.ShouldNotBeNull();
            lockedFor!.Value.TotalMinutes.ShouldBeInRange(expected - 0.2, expected + 0.2);
        }
    }
}

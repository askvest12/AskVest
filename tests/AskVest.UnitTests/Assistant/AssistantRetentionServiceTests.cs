using AskVest.Modules.Assistant.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Assistant;

public sealed class AssistantRetentionServiceTests
{
    [Fact]
    public async Task Failed_Run_Logs_And_The_Service_Still_Stops()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IServiceScopeFactory>(new ThrowingScopeFactory());
        var service = new AssistantRetentionService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AssistantRetentionService>.Instance);

        using var stop = new CancellationTokenSource();
        await service.StartAsync(stop.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);
        service.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeTrue();
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("scope failed");
    }
}

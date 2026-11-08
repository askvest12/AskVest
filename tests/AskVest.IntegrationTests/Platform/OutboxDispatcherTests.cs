using System.Text.Json;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Platform;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class OutboxDispatcherTests
{
    private readonly PostgresFixture _fixture;

    public OutboxDispatcherTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Delivers_Each_Message_To_Its_Handler_Once()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var ping = new RecordingHandler("test.ping");
        var pong = new RecordingHandler("test.pong");
        using var dispatcher = CreateDispatcher(clock, [ping, pong]);
        await EnqueueAsync("test.ping", """{"n":1}""");
        await EnqueueAsync("test.ping", """{"n":2}""");
        await EnqueueAsync("test.pong", """{"n":3}""");

        await dispatcher.DispatchCycleAsync(TestContext.Current.CancellationToken);

        ping.Payloads.OrderBy(payload => payload).ShouldBe([1, 2]);
        pong.Payloads.ShouldBe([3]);
        (await GetRowAsync("type = 'test.ping' AND status = 'processed' AND outcome = 'handled'")).ShouldBe(2L);
    }

    [Fact]
    public async Task Failing_Handler_Retries_With_Growing_Delay_Then_Dies()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var failing = new FailingHandler("test.fail");
        using var dispatcher = CreateDispatcher(clock, [failing], maxAttempts: 3);
        await EnqueueAsync("test.fail", """{"n":1}""");

        await dispatcher.DispatchCycleAsync(TestContext.Current.CancellationToken);
        var first = await GetMessageAsync("test.fail");
        Convert.ToInt32(first["attempts"]).ShouldBe(1);
        first["status"].ShouldBe("pending");
        var firstDelay = AsOffset(first["available_at"]!).Subtract(clock.GetUtcNow());
        firstDelay.ShouldBe(TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromSeconds(3));
        await dispatcher.DispatchCycleAsync(TestContext.Current.CancellationToken);
        var second = await GetMessageAsync("test.fail");
        Convert.ToInt32(second["attempts"]).ShouldBe(2);
        AsOffset(second["available_at"]!).Subtract(clock.GetUtcNow())
            .ShouldBe(TimeSpan.FromSeconds(4));

        clock.Advance(TimeSpan.FromSeconds(5));
        await dispatcher.DispatchCycleAsync(TestContext.Current.CancellationToken);
        var third = await GetMessageAsync("test.fail");
        third["status"].ShouldBe("dead");
        Convert.ToInt32(third["attempts"]).ShouldBe(3);
        third["last_error"].ShouldNotBeNull();
        failing.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task Unknown_Type_Becomes_Processed_With_NoHandler()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var dispatcher = CreateDispatcher(clock, []);
        await EnqueueAsync("test.unknown", """{"n":1}""");

        await dispatcher.DispatchCycleAsync(TestContext.Current.CancellationToken);

        var row = await GetMessageAsync("test.unknown");
        row["status"].ShouldBe("processed");
        row["outcome"].ShouldBe("no_handler");
    }

    [Fact]
    public async Task Expired_Lease_Can_Be_Reclaimed()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHandler("test.leased");
        using var dispatcher = CreateDispatcher(clock, [handler]);
        await EnqueueAsync("test.leased", """{"n":1}""");
        await Database.ExecuteAsync(
            _fixture.OwnerConnectionString,
            "UPDATE platform.outbox_messages SET lease_until = '2025-12-31T00:00:00Z' WHERE type = 'test.leased'",
            TestContext.Current.CancellationToken);

        await dispatcher.DispatchCycleAsync(TestContext.Current.CancellationToken);

        handler.Payloads.ShouldBe([1]);
    }

    [Fact]
    public async Task Two_Dispatchers_Never_Deliver_The_Same_Message()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHandler("test.race");
        using var first = CreateDispatcher(clock, [handler]);
        using var second = CreateDispatcher(clock, [handler]);
        for (var i = 0; i < 20; i++)
        {
            await EnqueueAsync("test.race", $$"""{"n":{{i}}}""");
        }

        await Task.WhenAll(
            first.DispatchCycleAsync(TestContext.Current.CancellationToken),
            second.DispatchCycleAsync(TestContext.Current.CancellationToken));

        handler.Payloads.Count.ShouldBe(20);
        handler.Payloads.Distinct().Count().ShouldBe(20);
    }

    [Fact]
    public async Task Processed_Messages_Are_Purged_After_Retention_And_Dead_Are_Kept()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        using var dispatcher = CreateDispatcher(clock, [], processedRetentionDays: 7);
        await EnqueueAsync("test.old_processed", """{"n":1}""");
        await EnqueueAsync("test.dead", """{"n":2}""");
        await Database.ExecuteAsync(
            _fixture.OwnerConnectionString,
            """
            UPDATE platform.outbox_messages SET status = 'processed', processed_at = '2026-01-01T00:00:00Z', outcome = 'handled'
            WHERE type = 'test.old_processed';
            UPDATE platform.outbox_messages SET status = 'dead', processed_at = '2026-01-01T00:00:00Z', outcome = 'dead'
            WHERE type = 'test.dead';
            """,
            TestContext.Current.CancellationToken);

        await dispatcher.DispatchCycleAsync(TestContext.Current.CancellationToken);

        (await GetRowAsync("type = 'test.old_processed'")).ShouldBe(0L);
        (await GetRowAsync("type = 'test.dead' AND status = 'dead'")).ShouldBe(1L);
    }

    [Fact]
    public async Task Error_Fields_Never_Contain_Payload_Content()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var failing = new FailingHandler("test.secret");
        using var dispatcher = CreateDispatcher(clock, [failing], maxAttempts: 1);
        await EnqueueAsync("test.secret", """{"token":"payload-secret-abc123"}""");

        await dispatcher.DispatchCycleAsync(TestContext.Current.CancellationToken);

        var row = await GetMessageAsync("test.secret");
        row["status"].ShouldBe("dead");
        var lastError = (string?)row["last_error"];
        lastError.ShouldNotBeNull();
        lastError.ShouldNotContain("payload-secret-abc123");
        lastError.Length.ShouldBeLessThanOrEqualTo(256);
    }

    private OutboxDispatcher CreateDispatcher(
        FakeTimeProvider clock,
        IReadOnlyList<IOutboxHandler> handlers,
        int maxAttempts = 8,
        int processedRetentionDays = 7)
    {
        var services = new ServiceCollection();
        foreach (var handler in handlers)
        {
            services.AddSingleton(handler);
        }

        var provider = services.BuildServiceProvider();
        var dataSource = NpgsqlDataSource.Create(_fixture.OwnerConnectionString);
        var options = Options.Create(new OutboxOptions
        {
            DispatchIntervalSeconds = 2,
            BatchSize = 50,
            LeaseSeconds = 60,
            MaxAttempts = maxAttempts,
            BackoffCapMinutes = 15,
            ProcessedRetentionDays = processedRetentionDays,
        });
        return new OutboxDispatcher(
            dataSource,
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            clock,
            NullLogger<OutboxDispatcher>.Instance);
    }

    private async Task EnqueueAsync(string type, string payloadJson)
    {
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO platform.outbox_messages
                (id, type, payload, occurred_at, available_at, attempts, status)
            VALUES (@id, @type, @payload::jsonb, @now, @now, 0, 'pending')
            """;
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("payload", payloadJson);
        command.Parameters.AddWithValue("now", DateTimeOffset.UnixEpoch);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DateTimeOffset AsOffset(object value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        _ => throw new InvalidCastException($"Cannot read {value.GetType().Name} as a timestamp."),
    };

    private async Task<Dictionary<string, object?>> GetMessageAsync(string type)
    {
        var rows = await Database.QueryAsync(
            _fixture.OwnerConnectionString,
            $"SELECT * FROM platform.outbox_messages WHERE type = '{type}' ORDER BY occurred_at DESC LIMIT 1",
            TestContext.Current.CancellationToken);
        return rows[0];
    }

    private async Task<long> GetRowAsync(string where)
    {
        var rows = await Database.QueryAsync(
            _fixture.OwnerConnectionString,
            $"SELECT count(*) AS n FROM platform.outbox_messages WHERE {where}",
            TestContext.Current.CancellationToken);
        return Convert.ToInt64(rows[0]["n"]);
    }

    private sealed class RecordingHandler(string messageType) : IOutboxHandler
    {
        private readonly List<int> _payloads = [];

        public string MessageType => messageType;

        public IReadOnlyList<int> Payloads
        {
            get
            {
                lock (_payloads)
                {
                    return [.. _payloads];
                }
            }
        }

        public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            lock (_payloads)
            {
                _payloads.Add(payload.GetProperty("n").GetInt32());
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FailingHandler(string messageType) : IOutboxHandler
    {
        private int _calls;

        public string MessageType => messageType;

        public int Calls => _calls;

        public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("handler failed for test");
        }
    }
}

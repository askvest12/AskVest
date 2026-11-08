using System.Data;
using AskVest.Modules.Practice.Configuration;
using AskVest.Modules.Practice.Domain;
using AskVest.Modules.Practice.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Practice.Services;

/// <summary>
/// Pages open stop-loss orders and triggers the ones whose latest usable price
/// is at or below the stop. Each order evaluates inside its own transaction
/// scope through PracticeOrderService, so one failure never stops the rest.
/// </summary>
internal sealed class PracticeStopEvaluator(
    IServiceScopeFactory scopeFactory,
    IOptions<PracticeOptions> options,
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    ILogger<PracticeStopEvaluator> logger) : BackgroundService
{
    internal const long AdvisoryLockKey = 2026110802L;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunLockedCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Practice stop evaluation cycle failed unexpectedly.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.Value.StopEvaluationIntervalSeconds),
                    timeProvider,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task<bool> RunCycleAsync(CancellationToken cancellationToken)
        => await RunLockedCycleAsync(cancellationToken);

    private async Task<bool> RunLockedCycleAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection))
        {
            lockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            var acquired = await lockCommand.ExecuteScalarAsync(cancellationToken);
            if (acquired is not true)
            {
                return false;
            }
        }

        try
        {
            await EvaluateAsync(cancellationToken);
            return true;
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            unlockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private const int PageSize = 200;

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var triggered = 0;
        var failed = 0;
        var evaluated = 0;
        DateTimeOffset? cursorAt = null;
        Guid cursorId = Guid.Empty;

        while (true)
        {
            var page = await OpenStopOrderIdsAsync(cursorAt, cursorId, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            evaluated += page.Count;
            var last = page[^1];
            cursorAt = last.CreatedAt;
            cursorId = last.Id;

            foreach (var orderId in page.Select(row => row.Id))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var orders = scope.ServiceProvider.GetRequiredService<PracticeOrderService>();
                    var result = await orders.TriggerStopAsync(orderId, cancellationToken);
                    if (result.IsFailure)
                    {
                        failed++;
                        logger.LogWarning(
                            "Practice stop evaluation failed for order {OrderId}: {ErrorCode}",
                            orderId, result.Error!.Code);
                    }
                    else if (result.Value)
                    {
                        triggered++;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed++;
                    logger.LogWarning(exception, "Practice stop evaluation threw for order {OrderId}.", orderId);
                }
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Practice stop evaluation completed: {Open} open, {Triggered} triggered, {Failed} failed.",
                evaluated, triggered, failed);
        }
    }

    // Keyset paging over (created_at, id) so orders that fail stay open without
    // trapping the cursor and orders created mid-cycle wait for the next cycle.
    private async Task<IReadOnlyList<(Guid Id, DateTimeOffset CreatedAt)>> OpenStopOrderIdsAsync(
        DateTimeOffset? cursorAt,
        Guid cursorId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();
        var hasCursor = cursorAt is not null;
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, created_at FROM practice.practice_orders
            WHERE state = 'open' AND type = 'stop_loss'
              AND (@hasCursor = FALSE
                   OR created_at > @cursorAt::timestamptz
                   OR (created_at = @cursorAt::timestamptz AND id > @cursorId::uuid))
            ORDER BY created_at ASC, id ASC
            LIMIT @limit
            """;
        var hasCursorParameter = command.CreateParameter();
        hasCursorParameter.ParameterName = "hasCursor";
        hasCursorParameter.Value = hasCursor;
        command.Parameters.Add(hasCursorParameter);
        var cursorAtParameter = command.CreateParameter();
        cursorAtParameter.ParameterName = "cursorAt";
        cursorAtParameter.Value = hasCursor ? cursorAt!.Value : DBNull.Value;
        command.Parameters.Add(cursorAtParameter);
        var cursorIdParameter = command.CreateParameter();
        cursorIdParameter.ParameterName = "cursorId";
        cursorIdParameter.Value = hasCursor ? cursorId : DBNull.Value;
        command.Parameters.Add(cursorIdParameter);
        var limitParameter = command.CreateParameter();
        limitParameter.ParameterName = "limit";
        limitParameter.Value = PageSize;
        command.Parameters.Add(limitParameter);

        var rows = new List<(Guid Id, DateTimeOffset CreatedAt)>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    await reader.GetFieldValueAsync<Guid>(0, cancellationToken),
                    await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken)));
            }
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }

        return rows;
    }
}

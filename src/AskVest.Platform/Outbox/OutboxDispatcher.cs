using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Platform;

internal sealed class OutboxDispatcher(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox dispatch cycle failed.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.Value.DispatchIntervalSeconds),
                    timeProvider,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task DispatchCycleAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var claimed = await ClaimAsync(settings, now, cancellationToken);
        foreach (var message in claimed)
        {
            await DeliverAsync(message, settings, cancellationToken);
        }

        await PurgeProcessedAsync(settings, now, cancellationToken);
    }

    private async Task<IReadOnlyList<ClaimedMessage>> ClaimAsync(
        OutboxOptions settings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var leaseUntil = now + TimeSpan.FromSeconds(settings.LeaseSeconds);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE platform.outbox_messages
            SET lease_until = @leaseUntil, attempts = attempts + 1
            WHERE status = @pending
              AND available_at <= @now
              AND (lease_until IS NULL OR lease_until <= @now)
              AND id IN (
                  SELECT id FROM platform.outbox_messages
                  WHERE status = @pending
                    AND available_at <= @now
                    AND (lease_until IS NULL OR lease_until <= @now)
                  ORDER BY occurred_at
                  LIMIT @batchSize
                  FOR UPDATE SKIP LOCKED)
            RETURNING id, type, payload, attempts
            """;
        command.Parameters.AddWithValue("leaseUntil", leaseUntil);
        command.Parameters.AddWithValue("pending", OutboxMessage.StatusPending);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batchSize", settings.BatchSize);

        var messages = new List<ClaimedMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new ClaimedMessage(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return messages;
    }

    private async Task DeliverAsync(
        ClaimedMessage message,
        OutboxOptions settings,
        CancellationToken cancellationToken)
    {
        string? outcome = null;
        string? error = null;
        var dead = false;
        try
        {
            outcome = await InvokeHandlerAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAsync(message, settings, cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            var detail = $"{exception.GetType().Name}: {exception.Message}";
            error = detail[..Math.Min(200, detail.Length)];
            if (message.Attempts >= settings.MaxAttempts)
            {
                dead = true;
                logger.LogError(exception, "Outbox message {MessageId} of type {MessageType} dead-lettered.", message.Id, message.MessageType);
            }
        }

        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (dead)
        {
            command.CommandText = """
                UPDATE platform.outbox_messages
                SET status = @dead, processed_at = @now, outcome = @outcome, last_error = @error,
                    lease_until = NULL, available_at = @now
                WHERE id = @id
                """;
            command.Parameters.AddWithValue("dead", OutboxMessage.StatusDead);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("outcome", OutboxMessage.StatusDead);
            command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("id", message.Id);
        }
        else if (outcome is null)
        {
            var delay = BackoffDelay(settings, message.Attempts);
            command.CommandText = """
                UPDATE platform.outbox_messages
                SET available_at = @availableAt, lease_until = NULL, last_error = @error
                WHERE id = @id
                """;
            command.Parameters.AddWithValue("availableAt", now + delay);
            command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("id", message.Id);
        }
        else
        {
            command.CommandText = """
                UPDATE platform.outbox_messages
                SET status = @processed, processed_at = @now, outcome = @outcome,
                    lease_until = NULL, last_error = NULL
                WHERE id = @id
                """;
            command.Parameters.AddWithValue("processed", OutboxMessage.StatusProcessed);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("outcome", outcome);
            command.Parameters.AddWithValue("id", message.Id);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string> InvokeHandlerAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider
            .GetServices<IOutboxHandler>()
            .FirstOrDefault(h => h.MessageType == message.MessageType);
        if (handler is null)
        {
            return OutboxMessage.OutcomeNoHandler;
        }

        using var document = JsonDocument.Parse(message.Payload);
        await handler.HandleAsync(document.RootElement.Clone(), cancellationToken);
        return OutboxMessage.OutcomeHandled;
    }

    private async Task ReleaseAsync(
        ClaimedMessage message,
        OutboxOptions settings,
        CancellationToken cancellationToken)
    {
        var delay = BackoffDelay(settings, message.Attempts);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE platform.outbox_messages
            SET available_at = @availableAt, lease_until = NULL
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("availableAt", timeProvider.GetUtcNow() + delay);
        command.Parameters.AddWithValue("id", message.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task PurgeProcessedAsync(
        OutboxOptions settings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = now - TimeSpan.FromDays(settings.ProcessedRetentionDays);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM platform.outbox_messages
            WHERE id IN (
                SELECT id FROM platform.outbox_messages
                WHERE status = @processed AND processed_at < @cutoff
                LIMIT 500)
            """;
        command.Parameters.AddWithValue("processed", OutboxMessage.StatusProcessed);
        command.Parameters.AddWithValue("cutoff", cutoff);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TimeSpan BackoffDelay(OutboxOptions settings, int attempts)
    {
        var seconds = settings.DispatchIntervalSeconds * Math.Pow(2, attempts - 1);
        var cap = TimeSpan.FromMinutes(settings.BackoffCapMinutes).TotalSeconds;
        return TimeSpan.FromSeconds(Math.Min(seconds, cap));
    }

    private sealed record ClaimedMessage(Guid Id, string MessageType, string Payload, int Attempts);
}

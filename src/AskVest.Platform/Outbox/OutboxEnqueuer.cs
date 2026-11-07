using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Platform;

internal sealed class OutboxEnqueuer(PlatformDbContext db, TimeProvider timeProvider) : IOutbox
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task EnqueueAsync(
        string messageType,
        object payload,
        string? dedupeKey = null,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var now = timeProvider.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO platform.outbox_messages
                (id, type, payload, occurred_at, available_at, attempts, status, dedupe_key)
            VALUES
                ({Guid.CreateVersion7()}, {messageType}, {json}::jsonb, {now}, {now}, 0,
                 {OutboxMessage.StatusPending}, {dedupeKey})
            ON CONFLICT (dedupe_key) DO NOTHING
            """,
            cancellationToken);
    }
}

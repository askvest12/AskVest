namespace AskVest.Platform;

public interface IOutbox
{
    Task EnqueueAsync(
        string messageType,
        object payload,
        string? dedupeKey = null,
        CancellationToken cancellationToken = default);
}

namespace AskVest.Api;

internal enum IdempotencyBeginResult
{
    Acquired,
    Replay,
    InProgress,
    KeyReused,
}

internal sealed record IdempotencyStoredResponse(int StatusCode, string? ContentType, string? Body, bool BodyOmitted);

internal interface IIdempotencyStore
{
    Task<IdempotencyBeginResult> BeginAsync(
        string scope,
        string key,
        string method,
        string path,
        string fingerprint,
        TimeSpan lockDuration,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    Task<IdempotencyStoredResponse?> GetCompletedAsync(
        string scope,
        string key,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        string scope,
        string key,
        IdempotencyStoredResponse response,
        CancellationToken cancellationToken);

    Task ReleaseAsync(string scope, string key, CancellationToken cancellationToken);

    Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

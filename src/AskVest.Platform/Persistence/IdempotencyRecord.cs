namespace AskVest.Platform;

public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }

    public required string Scope { get; set; }

    public required string IdempotencyKey { get; set; }

    public required string RequestMethod { get; set; }

    public required string RequestPath { get; set; }

    public required string RequestFingerprint { get; set; }

    public required string State { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public int? ResponseStatusCode { get; set; }

    public string? ResponseContentType { get; set; }

    public string? ResponseBody { get; set; }

    public bool ResponseBodyOmitted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

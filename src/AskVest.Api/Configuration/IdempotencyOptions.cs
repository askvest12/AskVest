using System.ComponentModel.DataAnnotations;

namespace AskVest.Api;

internal sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    [Range(1, 24 * 30)]
    public int RetentionHours { get; set; } = 24;

    [Range(1, 600)]
    public int LockSeconds { get; set; } = 60;

    [Range(1024, 10 * 1024 * 1024)]
    public int MaxStoredResponseBytes { get; set; } = 262144;
}

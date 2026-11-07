using System.ComponentModel.DataAnnotations;

namespace AskVest.Platform;

public sealed class OutboxOptions
{
    public const string SectionName = "Ledger:Outbox";

    [Range(1, 300)]
    public int DispatchIntervalSeconds { get; set; } = 2;

    [Range(1, 500)]
    public int BatchSize { get; set; } = 50;

    [Range(5, 3600)]
    public int LeaseSeconds { get; set; } = 60;

    [Range(1, 50)]
    public int MaxAttempts { get; set; } = 8;

    [Range(1, 1440)]
    public int BackoffCapMinutes { get; set; } = 15;

    [Range(1, 365)]
    public int ProcessedRetentionDays { get; set; } = 7;
}

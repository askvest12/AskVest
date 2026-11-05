using System.ComponentModel.DataAnnotations;

namespace AskVest.Api;

internal sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public GlobalRateLimitOptions Global { get; set; } = new();

    public StrictRateLimitOptions Strict { get; set; } = new();
}

internal sealed class GlobalRateLimitOptions
{
    [Range(1, 100000)]
    public int PermitLimit { get; set; } = 120;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}

internal sealed class StrictRateLimitOptions
{
    [Range(1, 10000)]
    public int PermitLimit { get; set; } = 10;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}

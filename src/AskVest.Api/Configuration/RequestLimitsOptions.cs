using System.ComponentModel.DataAnnotations;

namespace AskVest.Api;

internal sealed class RequestLimitsOptions
{
    public const string SectionName = "RequestLimits";

    [Range(1024, 10 * 1024 * 1024)]
    public long MaxBodyBytes { get; set; } = 1048576;

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;
}

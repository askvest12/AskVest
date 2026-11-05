using System.ComponentModel.DataAnnotations;

namespace AskVest.Platform;

public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; set; } = 30;
}

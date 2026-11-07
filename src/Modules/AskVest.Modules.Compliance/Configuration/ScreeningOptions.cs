namespace AskVest.Modules.Compliance.Configuration;

public sealed class ScreeningOptions
{
    public const string DefaultProvider = "fake";

    public string Provider { get; set; } = DefaultProvider;
    public string? HitNameMarker { get; set; }
}

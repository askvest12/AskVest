namespace AskVest.Modules.Compliance.Configuration;

public sealed class ComplianceDocumentOptions
{
    public string Type { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public Uri? Url { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string RequiredFor { get; set; } = string.Empty;
}

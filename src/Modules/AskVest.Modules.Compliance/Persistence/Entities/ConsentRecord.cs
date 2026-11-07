namespace AskVest.Modules.Compliance.Persistence.Entities;

public sealed class ConsentRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public DateTimeOffset AcceptedAt { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
}

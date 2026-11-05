namespace AskVest.Modules.Identity.Persistence.Entities;

public sealed class Device
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string InstallationId { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string? AppVersion { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? TrustedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public uint Xmin { get; set; }
}

namespace AskVest.Modules.Identity.Persistence.Entities;

public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset IdleExpiresAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    public uint Xmin { get; set; }
}

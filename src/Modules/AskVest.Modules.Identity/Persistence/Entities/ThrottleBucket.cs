using AskVest.Modules.Identity.Domain.Enums;

namespace AskVest.Modules.Identity.Persistence.Entities;

public sealed class ThrottleBucket
{
    public Guid Id { get; set; }
    public ThrottleKind Kind { get; set; }
    public string KeyHash { get; set; } = string.Empty;
    public int FailureCount { get; set; }
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public int LockRepeatCount { get; set; }
    public uint Xmin { get; set; }
}

using AskVest.Modules.Identity.Domain.Enums;

namespace AskVest.Modules.Identity.Persistence.Entities;

public sealed class UserAccount
{
    public Guid Id { get; set; }
    public string NormalizedEmail { get; set; } = string.Empty;
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public int PasswordPepperVersion { get; set; }
    public string? PhoneE164 { get; set; }
    public DateTimeOffset? PhoneVerifiedAt { get; set; }
    public UserStatus Status { get; set; }
    public string? PinHash { get; set; }
    public DateTimeOffset? PinSetAt { get; set; }
    public int PinFailedCount { get; set; }
    public DateTimeOffset? PinLockedUntil { get; set; }
    public DateTimeOffset? DeletionRequestedAt { get; set; }
    public DateTimeOffset? DeletionScheduledFor { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Xmin { get; set; }
}

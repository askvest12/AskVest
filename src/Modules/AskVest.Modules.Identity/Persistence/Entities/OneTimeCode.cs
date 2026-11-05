using AskVest.Modules.Identity.Domain.Enums;

namespace AskVest.Modules.Identity.Persistence.Entities;

public sealed class OneTimeCode
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public OneTimeCodePurpose Purpose { get; set; }
    public CodeDestinationType DestinationType { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public int AttemptsUsed { get; set; }
    public int AttemptsAllowed { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public Guid? DeviceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public uint Xmin { get; set; }
}

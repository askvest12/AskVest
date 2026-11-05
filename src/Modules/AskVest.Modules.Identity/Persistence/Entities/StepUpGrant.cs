namespace AskVest.Modules.Identity.Persistence.Entities;

public sealed class StepUpGrant
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string? BindingHash { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public uint Xmin { get; set; }
}

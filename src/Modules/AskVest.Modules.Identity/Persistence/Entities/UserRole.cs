namespace AskVest.Modules.Identity.Persistence.Entities;

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public string RoleName { get; set; } = string.Empty;
}

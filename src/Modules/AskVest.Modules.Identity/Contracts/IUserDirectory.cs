namespace AskVest.Modules.Identity.Contracts;

/// <summary>
/// Read-only view of an account's standing for other modules. Exposes no
/// credentials or personal data.
/// </summary>
public sealed record UserAccountSnapshot(bool IsActive, bool EmailVerified);

/// <summary>
/// Confirms whether a user id exists and reports its active and verified
/// state. Returns null when no account exists for the id.
/// </summary>
public interface IUserDirectory
{
    Task<UserAccountSnapshot?> FindAsync(Guid userId, CancellationToken cancellationToken = default);
}

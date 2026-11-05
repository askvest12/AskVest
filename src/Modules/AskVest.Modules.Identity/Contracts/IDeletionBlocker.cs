namespace AskVest.Modules.Identity.Contracts;

/// <summary>
/// Implemented by other modules that must be able to block account deletion
/// (for example open positions, pending withdrawals or earn enrollments).
/// The Identity module invokes every registered blocker during a deletion
/// request and refuses deletion while any blocker reports reasons.
/// </summary>
public interface IDeletionBlocker
{
    /// <summary>
    /// Returns human-readable reasons that currently block deletion for the
    /// user. An empty list means deletion is not blocked by this module.
    /// </summary>
    Task<IReadOnlyList<string>> GetBlockReasonsAsync(Guid userId, CancellationToken cancellationToken = default);
}

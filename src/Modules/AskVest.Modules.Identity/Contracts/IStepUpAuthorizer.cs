using AskVest.SharedKernel;

namespace AskVest.Modules.Identity.Contracts;

/// <summary>
/// Consumes single-use step-up grants on behalf of other modules.
/// </summary>
public interface IStepUpAuthorizer
{
    /// <summary>
    /// Atomically consumes a grant for the given user, session and purpose.
    /// <paramref name="binding"/> is the raw value the caller binds the grant to
    /// (for example an approved quote); pass null when the grant was issued
    /// without a binding. Any mismatch answers <c>auth.step_up_invalid</c>.
    /// </summary>
    Task<Result> ConsumeAsync(
        Guid userId,
        Guid sessionId,
        string purpose,
        string? binding,
        string token,
        CancellationToken cancellationToken = default);
}

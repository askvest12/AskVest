using AskVest.SharedKernel;

namespace AskVest.Modules.Funding.Contracts;

/// <summary>
/// The funding contract for other modules. Operator methods resolve review,
/// quarantine, restriction and pause cases that Phase 15 surfaces; none of
/// them is reachable over HTTP. Amounts are decimal and serialized as
/// strings by callers.
/// </summary>
public interface IFundingOperator
{
    /// <summary>Approves a withdrawal under review and enqueues submission.</summary>
    Task<Result> ApproveWithdrawalAsync(Guid withdrawalId, CancellationToken cancellationToken = default);

    /// <summary>Rejects a withdrawal under review and releases the hold.</summary>
    Task<Result> RejectWithdrawalAsync(Guid withdrawalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a quarantined deposit: credit posts the deposit journal,
    /// otherwise the deposit is marked for return.
    /// </summary>
    Task<Result> ResolveDepositAsync(
        Guid depositId, bool credit, CancellationToken cancellationToken = default);

    /// <summary>Clears an open funding restriction for a user.</summary>
    Task<Result> ClearRestrictionAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Lifts a stored reserves or manual pause for a currency.</summary>
    Task<Result> ClearPauseAsync(string currency, CancellationToken cancellationToken = default);

    /// <summary>Reads the user's cash balances by currency.</summary>
    Task<Result<IReadOnlyList<FundingCashBalance>>> GetCashBalancesAsync(
        Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>One cash balance row for the operator and module contract.</summary>
public sealed record FundingCashBalance(string Currency, decimal Available, decimal Held);

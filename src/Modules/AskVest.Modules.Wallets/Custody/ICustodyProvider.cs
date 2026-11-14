using AskVest.SharedKernel;

namespace AskVest.Modules.Wallets.Custody;

/// <summary>
/// The only custody seam in the module. No private key, seed phrase or
/// signing material ever exists in this process: the provider controls the
/// assets and this interface is the whole surface. Implementations must be
/// idempotent on <see cref="CustodyWithdrawRequest.OurReference"/>, report
/// deposits with stable ids and explicit finality, and never return secrets.
/// </summary>
public interface ICustodyProvider
{
    /// <summary>The provider name used in configuration routes.</summary>
    string ProviderName { get; }

    /// <summary>Chain ids this provider instance serves.</summary>
    IReadOnlyList<string> SupportedChains { get; }

    /// <summary>Whether deposit instructions may carry a memo.</summary>
    bool SupportsMemo { get; }

    /// <summary>The provider's finality rule, for operator documentation.</summary>
    string FinalityRule { get; }

    /// <summary>
    /// Gets or creates the deposit instruction for a user reference and
    /// chain. Repeated calls for the same pair return the same instruction.
    /// </summary>
    Task<Result<CustodyDepositInstruction>> GetDepositInstructionAsync(
        string userReference,
        string chainId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists deposits for a chain since a cursor. Deposit ids are stable so
    /// repeated pages cannot duplicate a credit.
    /// </summary>
    Task<Result<CustodyDepositPage>> ListDepositsAsync(
        string chainId,
        string? cursor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a withdrawal. Our reference is the idempotency key; a repeated
    /// call must resolve to the same provider withdrawal. An unknown outcome
    /// is reported as <see cref="CustodySubmitKind.Unknown"/>, never guessed.
    /// </summary>
    Task<CustodySubmitResult> SubmitWithdrawalAsync(
        CustodyWithdrawRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls a previously submitted withdrawal by our reference.
    /// </summary>
    Task<CustodyWithdrawalStatus> GetWithdrawalStatusAsync(
        string ourReference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports custody holdings per chain and token contract for the
    /// reserves check.
    /// </summary>
    Task<Result<IReadOnlyList<CustodyHolding>>> GetHoldingsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Says whether an address is internal to the provider (for example a
    /// deposit or treasury address). Used to refuse withdrawals to ourselves.
    /// </summary>
    Task<Result<bool>> IsInternalAddressAsync(
        string chainId,
        string address,
        CancellationToken cancellationToken = default);
}

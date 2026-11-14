namespace AskVest.Modules.Wallets.Custody;

/// <summary>
/// The deposit instruction handed to a user: an opaque address plus an
/// optional memo. The provider reference stays internal and is never
/// exposed through the API.
/// </summary>
public sealed record CustodyDepositInstruction(
    string Address,
    string? Memo,
    string ProviderReference);

public enum CustodyDepositFinality
{
    Pending,
    Final,
}

/// <summary>
/// One deposit reported by the provider. The token travels as the chain id
/// plus the canonical contract address; this module never parses either.
/// </summary>
public sealed record CustodyDeposit(
    string ProviderKey,
    string ProviderDepositId,
    string DestinationAddress,
    string ChainId,
    string TokenContractAddress,
    decimal Quantity,
    string SenderAddress,
    string? SettlementReference,
    CustodyDepositFinality Finality,
    DateTimeOffset ObservedAt);

public sealed record CustodyDepositPage(
    IReadOnlyList<CustodyDeposit> Deposits,
    string? NextCursor);

/// <summary>
/// A withdrawal submission. <paramref name="OurReference"/> is our withdrawal
/// id and doubles as the provider idempotency key.
/// </summary>
public sealed record CustodyWithdrawRequest(
    string OurReference,
    string ChainId,
    string TokenContractAddress,
    int TokenDecimals,
    string DestinationAddress,
    decimal Quantity);

public enum CustodySubmitKind
{
    Acknowledged,
    Rejected,
    Unknown,
}

public sealed record CustodySubmitResult(
    CustodySubmitKind Kind,
    string? ProviderReference,
    string? ReasonCode);

public enum CustodyWithdrawalStatusKind
{
    Pending,
    Completed,
    Failed,
    NotFound,
    Unavailable,
}

/// <summary>
/// A withdrawal status poll. NotFound is never definitive: the withdrawal
/// may still arrive, so callers keep funds held.
/// </summary>
public sealed record CustodyWithdrawalStatus(
    CustodyWithdrawalStatusKind Kind,
    string? SettlementReference,
    string? ReasonCode);

/// <summary>
/// Custody holdings for one chain and token contract, in token units.
/// </summary>
public sealed record CustodyHolding(
    string ChainId,
    string TokenContractAddress,
    decimal Quantity);

namespace AskVest.Modules.Ledger.Contracts;

/// <summary>
/// One user's net movement per journal and asset, summed across the user's
/// available and held accounts so holds and releases net to zero. Amounts are
/// signed decimals; positive adds to the user, negative removes.
/// </summary>
public sealed record LedgerMovement(
    Guid JournalId,
    DateTimeOffset PostedAt,
    string ReasonCode,
    string ReferenceType,
    Guid? ReferenceId,
    string AssetCode,
    decimal Amount);

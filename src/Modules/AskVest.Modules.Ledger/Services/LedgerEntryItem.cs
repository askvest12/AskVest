using AskVest.SharedKernel;

namespace AskVest.Modules.Ledger.Services;

internal sealed record LedgerEntryItem(
    Guid EntryId,
    Guid JournalId,
    DateTimeOffset PostedAt,
    string ReasonCode,
    string ReferenceType,
    Guid? ReferenceId,
    string AssetCode,
    decimal Amount);

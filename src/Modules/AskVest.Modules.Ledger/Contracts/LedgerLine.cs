namespace AskVest.Modules.Ledger.Contracts;

public sealed record LedgerLine(LedgerAccountRef Account, string AssetCode, decimal Amount);

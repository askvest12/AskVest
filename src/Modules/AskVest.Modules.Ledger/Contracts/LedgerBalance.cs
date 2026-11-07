namespace AskVest.Modules.Ledger.Contracts;

public sealed record LedgerBalance(string AssetCode, decimal Available, decimal Held);

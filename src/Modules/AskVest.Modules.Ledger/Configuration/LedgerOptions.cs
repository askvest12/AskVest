using System.ComponentModel.DataAnnotations;

namespace AskVest.Modules.Ledger.Configuration;

public sealed class LedgerOptions
{
    public const string SectionName = "Ledger";

    [Range(1, 1440)]
    public int ReconciliationIntervalMinutes { get; set; } = 60;

    [Range(1, 10000)]
    public int ReconciliationPageSize { get; set; } = 1000;
}

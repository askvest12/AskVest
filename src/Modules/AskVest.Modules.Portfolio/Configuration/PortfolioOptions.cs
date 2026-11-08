using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;

namespace AskVest.Modules.Portfolio.Configuration;

public sealed class PortfolioOptions
{
    public const string SectionName = "Portfolio";

    public Collection<string> CashEquivalentAssets { get; } = ["USD", "USDC"];

    [Range(1, 500)]
    public int WatchlistMaximum { get; set; } = 50;

    [Range(1, 1_000_000)]
    public int MaxJournalsPerReplay { get; set; } = 20_000;

    [Range(0, 6)]
    public int DisplayDecimals { get; set; } = 2;
}

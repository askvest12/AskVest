namespace AskVest.Modules.Portfolio.Domain;

internal sealed record PortfolioReplayResult(
    IReadOnlyList<ReplayAssetState> Assets,
    IReadOnlyDictionary<string, decimal> IncomeByInstrument,
    decimal UnattributedIncome,
    bool HasInconsistency)
{
    public bool HasUnclassifiedMovements => Assets.Any(asset => asset.HasUnclassifiedMovements);
}

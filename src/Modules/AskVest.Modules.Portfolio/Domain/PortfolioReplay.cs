using AskVest.Modules.Ledger.Contracts;
using AskVest.SharedKernel;

namespace AskVest.Modules.Portfolio.Domain;

/// <summary>
/// Replays a user's net ledger movements into per-asset positions with weighted
/// average cost basis. Pure and deterministic: all inputs arrive as arguments.
/// </summary>
internal static class PortfolioReplay
{
    public static Result<PortfolioReplayResult> Run(
        IReadOnlyList<LedgerMovement> movements,
        IReadOnlyDictionary<string, AssetKind> assetKinds,
        Func<string, string?> instrumentOf,
        int maxJournals)
    {
        var journalCount = movements.Select(movement => movement.JournalId).Distinct().Count();
        if (journalCount > maxJournals)
        {
            return Result.Failure<PortfolioReplayResult>(new Error(
                ErrorCodes.ServiceUnavailable,
                "The portfolio history is too large to replay.",
                ErrorType.Unavailable));
        }

        var effectiveReason = movements
            .GroupBy(movement => movement.JournalId)
            .ToDictionary(group => group.Key, group => group.First().ReasonCode);

        // A reversal mirrors its original journal's amounts, so it is
        // classified by the inverse of the reason it reverses.
        foreach (var movement in movements.Where(m => m.ReasonCode == LedgerReasonCodes.Reversal))
        {
            if (movement.ReferenceType == "journal"
                && movement.ReferenceId is { } originalId
                && effectiveReason.TryGetValue(originalId, out var originalReason))
            {
                effectiveReason[movement.JournalId] = InverseReason(originalReason);
            }
        }

        var assets = new Dictionary<string, ReplayAssetState>(StringComparer.Ordinal);
        var income = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var unattributedIncome = 0m;
        var hasInconsistency = false;

        var journals = movements
            .GroupBy(movement => movement.JournalId)
            .OrderBy(group => group.Min(movement => movement.PostedAt))
            .ThenBy(group => group.Key);
        foreach (var journal in journals)
        {
            var reason = effectiveReason[journal.Key];
            var rows = journal.Where(movement => movement.Amount != 0m).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            var positions = rows.Where(row => IsPosition(row, assetKinds)).ToList();
            var cash = rows.Where(row => IsCash(row, assetKinds)).ToList();

            switch (reason)
            {
                case LedgerReasonCodes.TradeBuy:
                    ApplyTradeBuy(positions, cash, assets, instrumentOf);
                    break;
                case LedgerReasonCodes.TradeSell:
                    ApplyTradeSell(positions, cash, assets, instrumentOf, ref hasInconsistency);
                    break;
                case LedgerReasonCodes.CorporateActionSplit:
                    ApplySplit(positions, assets, instrumentOf, ref hasInconsistency);
                    break;
                case LedgerReasonCodes.Deposit:
                    foreach (var row in positions)
                    {
                        var state = StateFor(assets, row, instrumentOf);
                        state.Quantity += row.Amount;
                        state.HasUnclassifiedMovements = true;
                    }
                    break;
                case LedgerReasonCodes.Withdrawal:
                    ApplyWithdrawal(positions, assets, instrumentOf, ref hasInconsistency);
                    break;
                case LedgerReasonCodes.Dividend:
                    var amount = cash.Sum(row => row.Amount);
                    if (amount != 0m)
                    {
                        var reference = rows.First();
                        if (reference.ReferenceType == "instrument" && reference.ReferenceId is { } instrumentRef)
                        {
                            var key = instrumentRef.ToString();
                            income[key] = income.GetValueOrDefault(key) + amount;
                        }
                        else
                        {
                            unattributedIncome += amount;
                        }
                    }
                    break;
                default:
                    foreach (var row in positions)
                    {
                        var state = StateFor(assets, row, instrumentOf);
                        state.Quantity += row.Amount;
                        state.HasUnclassifiedMovements = true;
                    }
                    break;
            }
        }

        return Result.Success(new PortfolioReplayResult(
            assets.Values.OrderBy(asset => asset.AssetCode, StringComparer.Ordinal).ToList(),
            income,
            unattributedIncome,
            hasInconsistency));
    }

    private static void ApplyTradeBuy(
        List<LedgerMovement> positions,
        List<LedgerMovement> cash,
        Dictionary<string, ReplayAssetState> assets,
        Func<string, string?> instrumentOf)
    {
        var wellFormed = positions.Count == 1 && cash.Count == 1;
        if (!wellFormed)
        {
            MarkUnclassified(positions, assets, instrumentOf);
            return;
        }

        var position = positions[0];
        var cashMovement = cash.Sum(row => row.Amount);
        if (position.Amount <= 0m || cashMovement >= 0m)
        {
            MarkUnclassified(positions, assets, instrumentOf);
            return;
        }

        var state = StateFor(assets, position, instrumentOf);
        state.Quantity += position.Amount;
        state.TotalCost += -cashMovement;
    }

    private static void ApplyTradeSell(
        List<LedgerMovement> positions,
        List<LedgerMovement> cash,
        Dictionary<string, ReplayAssetState> assets,
        Func<string, string?> instrumentOf,
        ref bool hasInconsistency)
    {
        var wellFormed = positions.Count == 1 && cash.Count == 1;
        if (!wellFormed)
        {
            MarkUnclassified(positions, assets, instrumentOf);
            return;
        }

        var position = positions[0];
        var proceeds = cash.Sum(row => row.Amount);
        if (position.Amount >= 0m || proceeds <= 0m)
        {
            MarkUnclassified(positions, assets, instrumentOf);
            return;
        }

        var state = StateFor(assets, position, instrumentOf);
        var sold = -position.Amount;
        var applied = sold;
        if (sold > state.Quantity)
        {
            applied = state.Quantity;
            hasInconsistency = true;
        }

        var quantityBefore = state.Quantity;
        var removedCost = quantityBefore > 0m
            ? PortfolioRounding.RoundCost(state.TotalCost * applied / quantityBefore)
            : 0m;
        state.Quantity -= applied;
        state.TotalCost -= removedCost;
        state.RealizedProfit += proceeds - removedCost;

        if (state.Quantity <= 0m)
        {
            // Rounding dust on a closed position belongs to realized profit.
            state.RealizedProfit += state.TotalCost;
            state.TotalCost = 0m;
            state.Quantity = 0m;
        }

    }

    private static void ApplyWithdrawal(
        IReadOnlyList<LedgerMovement> positions,
        Dictionary<string, ReplayAssetState> assets,
        Func<string, string?> instrumentOf,
        ref bool hasInconsistency)
    {
        foreach (var row in positions)
        {
            if (row.Amount >= 0m)
            {
                continue;
            }

            var state = StateFor(assets, row, instrumentOf);
            var withdrawn = -row.Amount;
            var applied = withdrawn;
            if (withdrawn > state.Quantity)
            {
                applied = state.Quantity;
                hasInconsistency = true;
            }

            var quantityBefore = state.Quantity;
            var removedCost = quantityBefore > 0m
                ? PortfolioRounding.RoundCost(state.TotalCost * applied / quantityBefore)
                : 0m;
            state.Quantity -= applied;
            state.TotalCost -= removedCost;
            if (state.Quantity <= 0m)
            {
                state.TotalCost = 0m;
                state.Quantity = 0m;
            }
        }
    }

    private static void ApplySplit(
        IReadOnlyList<LedgerMovement> positions,
        Dictionary<string, ReplayAssetState> assets,
        Func<string, string?> instrumentOf,
        ref bool hasInconsistency)
    {
        foreach (var row in positions)
        {
            var state = StateFor(assets, row, instrumentOf);
            state.Quantity += row.Amount;
            if (state.Quantity < 0m)
            {
                hasInconsistency = true;
                state.RealizedProfit += state.TotalCost;
                state.TotalCost = 0m;
                state.Quantity = 0m;
            }
        }
    }

    private static void MarkUnclassified(
        IReadOnlyList<LedgerMovement> positions,
        Dictionary<string, ReplayAssetState> assets,
        Func<string, string?> instrumentOf)
    {
        foreach (var row in positions)
        {
            var state = StateFor(assets, row, instrumentOf);
            state.Quantity += row.Amount;
            state.HasUnclassifiedMovements = true;
        }
    }

    private static string InverseReason(string reason)
        => reason switch
        {
            LedgerReasonCodes.TradeBuy => LedgerReasonCodes.TradeSell,
            LedgerReasonCodes.TradeSell => LedgerReasonCodes.TradeBuy,
            _ => reason,
        };

    private static ReplayAssetState StateFor(
        Dictionary<string, ReplayAssetState> assets,
        LedgerMovement movement,
        Func<string, string?> instrumentOf)
    {
        if (!assets.TryGetValue(movement.AssetCode, out var state))
        {
            state = new ReplayAssetState { AssetCode = movement.AssetCode };
            assets[movement.AssetCode] = state;
        }

        state.InstrumentId ??= instrumentOf(movement.AssetCode);
        return state;
    }

    private static bool IsPosition(LedgerMovement movement, IReadOnlyDictionary<string, AssetKind> kinds)
        => !kinds.TryGetValue(movement.AssetCode, out var kind) || kind == AssetKind.Token;

    private static bool IsCash(LedgerMovement movement, IReadOnlyDictionary<string, AssetKind> kinds)
        => kinds.TryGetValue(movement.AssetCode, out var kind) && kind is AssetKind.Fiat or AssetKind.Stablecoin;
}

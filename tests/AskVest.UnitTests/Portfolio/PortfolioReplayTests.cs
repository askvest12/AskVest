using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Portfolio.Domain;
using AskVest.SharedKernel;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Portfolio;

public sealed class PortfolioReplayTests
{
    private static readonly Guid BuyJournal = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SellJournal = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InstrumentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly IReadOnlyDictionary<string, AssetKind> Kinds = new Dictionary<string, AssetKind>
    {
        ["USD"] = AssetKind.Fiat,
        ["USDC"] = AssetKind.Stablecoin,
        ["TOK"] = AssetKind.Token,
        ["TOK2"] = AssetKind.Token,
    };

    private static string? InstrumentOf(string assetCode) => assetCode switch
    {
        "TOK" or "TOK2" => InstrumentId.ToString(),
        _ => null,
    };

    private static LedgerMovement Movement(
        Guid journalId,
        string reason,
        string assetCode,
        decimal amount,
        string referenceType = "order",
        Guid? referenceId = null,
        DateTimeOffset? postedAt = null)
        => new(journalId, postedAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), reason, referenceType, referenceId, assetCode, amount);

    private static List<LedgerMovement> BuyJournalRows(decimal quantity, decimal cashOut, Guid? journalId = null, DateTimeOffset? at = null)
        =>
        [
            Movement(journalId ?? BuyJournal, LedgerReasonCodes.TradeBuy, "TOK", quantity, postedAt: at),
            Movement(journalId ?? BuyJournal, LedgerReasonCodes.TradeBuy, "USD", -cashOut, postedAt: at),
        ];

    private static List<LedgerMovement> SellJournalRows(decimal quantity, decimal cashIn, Guid? journalId = null, DateTimeOffset? at = null)
        =>
        [
            Movement(journalId ?? SellJournal, LedgerReasonCodes.TradeSell, "TOK", -quantity, postedAt: at),
            Movement(journalId ?? SellJournal, LedgerReasonCodes.TradeSell, "USD", cashIn, postedAt: at),
        ];

    private static PortfolioReplayResult Replay(IReadOnlyList<LedgerMovement> movements, int maxJournals = 20_000)
    {
        var result = PortfolioReplay.Run(movements, Kinds, InstrumentOf, maxJournals);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value!;
    }

    [Fact]
    public void Buy_Adds_Quantity_And_Cost_Including_Fee()
    {
        var result = Replay(BuyJournalRows(10m, 105m));
        var asset = result.Assets.ShouldHaveSingleItem();
        asset.AssetCode.ShouldBe("TOK");
        asset.InstrumentId.ShouldBe(InstrumentId.ToString());
        asset.Quantity.ShouldBe(10m);
        asset.TotalCost.ShouldBe(105m);
        asset.RealizedProfit.ShouldBe(0m);
        result.HasInconsistency.ShouldBeFalse();
        result.HasUnclassifiedMovements.ShouldBeFalse();
    }

    [Fact]
    public void Second_Buy_Updates_Weighted_Cost()
    {
        var second = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var at2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var movements = BuyJournalRows(10m, 100m)
            .Concat(BuyJournalRows(10m, 150m, second, at2))
            .ToList();
        var asset = Replay(movements).Assets.ShouldHaveSingleItem();
        asset.Quantity.ShouldBe(20m);
        asset.TotalCost.ShouldBe(250m);
    }

    [Fact]
    public void Sell_Removes_Proportional_Cost_And_Realizes_Proceeds()
    {
        var at2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var movements = BuyJournalRows(10m, 100m)
            .Concat(BuyJournalRows(10m, 150m, Guid.Parse("44444444-4444-4444-4444-444444444444"), at2))
            .Concat(SellJournalRows(10m, 80m, SellJournal, at2.AddDays(1)))
            .ToList();
        var asset = Replay(movements).Assets.ShouldHaveSingleItem();
        asset.Quantity.ShouldBe(10m);
        asset.TotalCost.ShouldBe(125m);
        asset.RealizedProfit.ShouldBe(-45m);
    }

    [Fact]
    public void Sell_To_Zero_Moves_Residual_Cost_To_Realized()
    {
        var at2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        // Cost carries beyond 6 decimals so the proportional removal leaves dust.
        var movements = BuyJournalRows(1m, 0.1234567m)
            .Concat(SellJournalRows(1m, 0.2m, SellJournal, at2))
            .ToList();
        var asset = Replay(movements).Assets.ShouldHaveSingleItem();
        asset.Quantity.ShouldBe(0m);
        asset.TotalCost.ShouldBe(0m);
        asset.RealizedProfit.ShouldBe(0.0765427m);
    }

    [Fact]
    public void Split_Changes_Quantity_Without_Cost()
    {
        var split = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var at2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var movements = BuyJournalRows(10m, 100m)
            .Concat(
            [
                Movement(split, LedgerReasonCodes.CorporateActionSplit, "TOK", 10m, "instrument", InstrumentId, at2),
            ])
            .ToList();
        var asset = Replay(movements).Assets.ShouldHaveSingleItem();
        asset.Quantity.ShouldBe(20m);
        asset.TotalCost.ShouldBe(100m);
        asset.RealizedProfit.ShouldBe(0m);
    }

    [Fact]
    public void Dividend_Counts_As_Income_For_Referenced_Instrument()
    {
        var dividend = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var movements = BuyJournalRows(10m, 100m)
            .Concat(
            [
                Movement(dividend, LedgerReasonCodes.Dividend, "USD", 5m, "instrument", InstrumentId),
            ])
            .ToList();
        var result = Replay(movements);
        result.IncomeByInstrument.ShouldBe(new Dictionary<string, decimal> { [InstrumentId.ToString()] = 5m });
        result.UnattributedIncome.ShouldBe(0m);
    }

    [Fact]
    public void Dividend_Without_Instrument_Reference_Is_Unattributed()
    {
        var dividend = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var result = Replay(
        [
            Movement(dividend, LedgerReasonCodes.Dividend, "USD", 5m, "account", null),
        ]);
        result.IncomeByInstrument.ShouldBeEmpty();
        result.UnattributedIncome.ShouldBe(5m);
    }

    [Fact]
    public void Cash_Only_Movements_Produce_No_Positions()
    {
        var deposit = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var result = Replay(
        [
            Movement(deposit, LedgerReasonCodes.Deposit, "USD", 1000m),
            Movement(deposit, LedgerReasonCodes.Deposit, "USDC", 500m),
        ]);
        result.Assets.ShouldBeEmpty();
        result.HasUnclassifiedMovements.ShouldBeFalse();
        result.HasInconsistency.ShouldBeFalse();
    }

    [Fact]
    public void Unclassified_Position_Movement_Changes_Quantity_Without_Cost()
    {
        var adjustment = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var result = Replay(
        [
            BuyJournalRows(10m, 100m)[0],
            Movement(adjustment, LedgerReasonCodes.Adjustment, "TOK", 5m),
        ]);
        var asset = result.Assets.ShouldHaveSingleItem();
        asset.Quantity.ShouldBe(15m);
        asset.TotalCost.ShouldBe(0m);
        result.HasUnclassifiedMovements.ShouldBeTrue();
    }

    [Fact]
    public void Malformed_Trade_Journal_Is_Unclassified()
    {
        var weird = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var result = Replay(
        [
            Movement(weird, LedgerReasonCodes.TradeBuy, "TOK", 10m),
            Movement(weird, LedgerReasonCodes.TradeBuy, "TOK2", 4m),
            Movement(weird, LedgerReasonCodes.TradeBuy, "USD", -140m),
        ]);
        result.Assets.Count.ShouldBe(2);
        result.Assets.ShouldAllBe(asset => asset.HasUnclassifiedMovements);
        result.Assets.ShouldAllBe(asset => asset.TotalCost == 0m);
    }

    [Fact]
    public void Oversell_Applies_Only_Held_Quantity_And_Flags()
    {
        var at2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var movements = BuyJournalRows(10m, 100m)
            .Concat(SellJournalRows(15m, 160m, SellJournal, at2))
            .ToList();
        var result = Replay(movements);
        var asset = result.Assets.ShouldHaveSingleItem();
        asset.Quantity.ShouldBe(0m);
        asset.RealizedProfit.ShouldBe(60m);
        result.HasInconsistency.ShouldBeTrue();
    }

    [Fact]
    public void Reversal_Of_Buy_Behaves_Like_A_Sell()
    {
        var reversal = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var at2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var movements = BuyJournalRows(10m, 100m)
            .Concat(
            [
                Movement(reversal, LedgerReasonCodes.Reversal, "TOK", -10m, "journal", BuyJournal, at2),
                Movement(reversal, LedgerReasonCodes.Reversal, "USD", 100m, "journal", BuyJournal, at2),
            ])
            .ToList();
        var asset = Replay(movements).Assets.ShouldHaveSingleItem();
        asset.Quantity.ShouldBe(0m);
        asset.TotalCost.ShouldBe(0m);
        asset.RealizedProfit.ShouldBe(0m);
    }

    [Fact]
    public void Input_Order_Does_Not_Change_Result()
    {
        var movements = BuyJournalRows(10m, 100m)
            .Concat(SellJournalRows(4m, 50m, SellJournal, new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)))
            .ToList();
        var forward = Replay(movements);
        var shuffled = Replay(movements.OrderBy(_ => Guid.CreateVersion7()).ToList());
        var a = forward.Assets.ShouldHaveSingleItem();
        var b = shuffled.Assets.ShouldHaveSingleItem();
        b.Quantity.ShouldBe(a.Quantity);
        b.TotalCost.ShouldBe(a.TotalCost);
        b.RealizedProfit.ShouldBe(a.RealizedProfit);
    }

    [Fact]
    public void Journal_Cap_Returns_Service_Unavailable()
    {
        var movements = Enumerable.Range(0, 5)
            .SelectMany(i => BuyJournalRows(1m, 1m, Guid.CreateVersion7(), new DateTimeOffset(2026, 1, 1, 0, 0, i, TimeSpan.Zero)))
            .ToList();
        var result = PortfolioReplay.Run(movements, Kinds, InstrumentOf, 4);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public void Exact_Decimal_Arithmetic()
    {
        var result = Replay(BuyJournalRows(3m, 33.333333m));
        var asset = result.Assets.ShouldHaveSingleItem();
        asset.TotalCost.ShouldBe(33.333333m);
    }

    [Fact]
    public void Token_Deposit_Adds_Quantity_Without_Cost_And_Flags_Unclassified()
    {
        var deposit = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var movements = BuyJournalRows(10m, 100m)
            .Concat([
                Movement(deposit, LedgerReasonCodes.Deposit, "TOK", 4m, "wallet_deposit", deposit),
            ])
            .ToList();
        var result = Replay(movements);
        var asset = result.Assets.ShouldHaveSingleItem();
        asset.Quantity.ShouldBe(14m);
        asset.TotalCost.ShouldBe(100m);
        asset.RealizedProfit.ShouldBe(0m);
        result.HasUnclassifiedMovements.ShouldBeTrue();
    }

    [Fact]
    public void Token_Withdrawal_Removes_Cost_Proportionally_With_No_Profit_Or_Flag()
    {
        var withdrawal = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var at2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var movements = BuyJournalRows(10m, 100m)
            .Concat(BuyJournalRows(10m, 150m, Guid.Parse("44444444-4444-4444-4444-444444444444"), at2))
            .Concat([
                Movement(withdrawal, LedgerReasonCodes.Withdrawal, "TOK", -10m, "wallet_withdrawal", withdrawal, at2.AddDays(1)),
            ])
            .ToList();
        var result = Replay(movements);
        var asset = result.Assets.ShouldHaveSingleItem();
        asset.Quantity.ShouldBe(10m);
        asset.TotalCost.ShouldBe(125m);
        asset.RealizedProfit.ShouldBe(0m);
        result.HasUnclassifiedMovements.ShouldBeFalse();
    }

    [Fact]
    public void Token_Withdrawal_To_Zero_Clears_Cost_Without_Realized_Profit()
    {
        var withdrawal = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var at2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var movements = BuyJournalRows(10m, 100m)
            .Concat([
                Movement(withdrawal, LedgerReasonCodes.Withdrawal, "TOK", -10m, "wallet_withdrawal", withdrawal, at2),
            ])
            .ToList();
        var asset = Replay(movements).Assets.ShouldHaveSingleItem();
        asset.Quantity.ShouldBe(0m);
        asset.TotalCost.ShouldBe(0m);
        asset.RealizedProfit.ShouldBe(0m);
    }
}

using AskVest.Modules.Practice.Domain;

namespace AskVest.Modules.Practice.Services;

internal static class PracticeText
{
    public static string ToDb(PracticeOrderSide side) => side == PracticeOrderSide.Buy ? "buy" : "sell";

    public static string ToDb(PracticeOrderType type) => type == PracticeOrderType.Market ? "market" : "stop_loss";

    public static string ToDb(PracticeOrderState state) => state switch
    {
        PracticeOrderState.Open => "open",
        PracticeOrderState.Filled => "filled",
        _ => "cancelled",
    };

    public static string ToDb(PracticeEventType type) => type switch
    {
        PracticeEventType.AccountStarted => "account_started",
        PracticeEventType.AccountReset => "account_reset",
        PracticeEventType.OrderFilled => "order_filled",
        PracticeEventType.OrderCancelled => "order_cancelled",
        _ => "stop_triggered",
    };

    public static PracticeOrderSide SideFromDb(string value) => value == "buy" ? PracticeOrderSide.Buy : PracticeOrderSide.Sell;

    public static PracticeOrderType TypeFromDb(string value) => value == "market" ? PracticeOrderType.Market : PracticeOrderType.StopLoss;

    public static PracticeOrderState StateFromDb(string value) => value switch
    {
        "open" => PracticeOrderState.Open,
        "filled" => PracticeOrderState.Filled,
        _ => PracticeOrderState.Cancelled,
    };

    public static PracticeEventType EventTypeFromDb(string value) => value switch
    {
        "account_started" => PracticeEventType.AccountStarted,
        "account_reset" => PracticeEventType.AccountReset,
        "order_filled" => PracticeEventType.OrderFilled,
        "order_cancelled" => PracticeEventType.OrderCancelled,
        _ => PracticeEventType.StopTriggered,
    };
}

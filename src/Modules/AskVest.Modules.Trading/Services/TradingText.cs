using AskVest.Modules.Trading.Domain;

namespace AskVest.Modules.Trading.Services;

internal static class TradingText
{
    public static string ToDb(OrderSide side) => side == OrderSide.Buy ? "buy" : "sell";

    public static string ToDb(OrderState state) => state switch
    {
        OrderState.PendingSubmission => "pending_submission",
        OrderState.Submitting => "submitting",
        OrderState.Submitted => "submitted",
        OrderState.Executed => "executed",
        OrderState.Failed => "failed",
        OrderState.Cancelled => "cancelled",
        _ => "needs_reconciliation",
    };

    public static string ToDb(QuoteState state) => state == QuoteState.Open ? "open" : "used";

    public static string ToDb(TradingEventType type) => type switch
    {
        TradingEventType.OrderPlaced => "order_placed",
        TradingEventType.OrderSubmitted => "order_submitted",
        TradingEventType.OrderExecuted => "order_executed",
        TradingEventType.OrderFailed => "order_failed",
        TradingEventType.OrderCancelled => "order_cancelled",
        TradingEventType.OrderNeedsReconciliation => "order_needs_reconciliation",
        _ => "order_reconciliation_resolved",
    };

    public static OrderSide SideFromDb(string value)
        => value == "buy" ? OrderSide.Buy : OrderSide.Sell;

    public static OrderState StateFromDb(string value) => value switch
    {
        "pending_submission" => OrderState.PendingSubmission,
        "submitting" => OrderState.Submitting,
        "submitted" => OrderState.Submitted,
        "executed" => OrderState.Executed,
        "failed" => OrderState.Failed,
        "cancelled" => OrderState.Cancelled,
        _ => OrderState.NeedsReconciliation,
    };

    public static QuoteState QuoteStateFromDb(string value)
        => value == "open" ? QuoteState.Open : QuoteState.Used;

    public static TradingEventType EventTypeFromDb(string value) => value switch
    {
        "order_placed" => TradingEventType.OrderPlaced,
        "order_submitted" => TradingEventType.OrderSubmitted,
        "order_executed" => TradingEventType.OrderExecuted,
        "order_failed" => TradingEventType.OrderFailed,
        "order_cancelled" => TradingEventType.OrderCancelled,
        "order_needs_reconciliation" => TradingEventType.OrderNeedsReconciliation,
        _ => TradingEventType.OrderReconciliationResolved,
    };
}

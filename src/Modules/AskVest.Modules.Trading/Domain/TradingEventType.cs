namespace AskVest.Modules.Trading.Domain;

public enum TradingEventType
{
    OrderPlaced,
    OrderSubmitted,
    OrderExecuted,
    OrderFailed,
    OrderCancelled,
    OrderNeedsReconciliation,
    OrderReconciliationResolved,
}

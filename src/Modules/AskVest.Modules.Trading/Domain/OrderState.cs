namespace AskVest.Modules.Trading.Domain;

/// <summary>
/// Order lifecycle. Submission and execution are separate states: an order
/// can be acknowledged by the gateway and still not be filled.
/// </summary>
public enum OrderState
{
    PendingSubmission,
    Submitting,
    Submitted,
    Executed,
    Failed,
    Cancelled,
    NeedsReconciliation,
}

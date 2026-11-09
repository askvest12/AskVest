using AskVest.Modules.Assistant.Policy;
using AskVest.Modules.Assistant.Tools;

namespace AskVest.Modules.Assistant.Agent;

/// <summary>
/// The outcome of one agent run. <see cref="Text"/> is null when the run
/// failed at the provider level and the endpoint must answer 503; a safe
/// fallback is still text with <see cref="PolicyBlocked"/> or the numeric
/// outcome set instead.
/// </summary>
internal sealed record AssistantTurnResult(
    string Outcome,
    bool PolicyBlocked,
    string? Text,
    IReadOnlyList<AssistantCitation> Citations,
    IReadOnlyList<FactCard> FactCards,
    IReadOnlyList<AssistantAction> Actions,
    AssistantAuditData Audit);

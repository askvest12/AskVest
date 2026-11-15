using AskVest.Modules.Funding.Domain;

namespace AskVest.Modules.Funding.Persistence.Entities;

/// <summary>
/// Append-only audit trail for funding state transitions. The application
/// role has no UPDATE or DELETE rights; proven by a database test.
/// </summary>
public sealed class FundingEvent
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string SubjectType { get; set; } = string.Empty;

    public Guid SubjectId { get; set; }

    public FundingEventType EventType { get; set; }

    public string? FromState { get; set; }

    public string? ToState { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string? CorrelationId { get; set; }

    public string? DetailCode { get; set; }
}

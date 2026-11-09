namespace AskVest.Modules.Assistant.Tools;

/// <summary>
/// Per-message tool invocation context. The user id always comes from the
/// authenticated session; tools never accept one as an argument.
/// </summary>
internal sealed class ToolContext(Guid userId, TimeProvider timeProvider)
{
    private int _nextResultId;

    public Guid UserId { get; } = userId;

    public TimeProvider TimeProvider { get; } = timeProvider;

    public string NextResultId() => $"r{++_nextResultId}";
}

namespace AskVest.Modules.Assistant.Persistence.Entities;

public sealed class Message
{
    public const string RoleUser = "user";
    public const string RoleAssistant = "assistant";

    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid UserId { get; set; }

    public string Role { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string Citations { get; set; } = "[]";

    public string FactCards { get; set; } = "[]";

    public string Actions { get; set; } = "[]";

    public string? PolicyOutcome { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? IdempotencyKey { get; set; }
}

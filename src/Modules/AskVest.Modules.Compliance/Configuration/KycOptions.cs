namespace AskVest.Modules.Compliance.Configuration;

public sealed class KycOptions
{
    public const string DefaultProvider = "fake";

    public string Provider { get; set; } = DefaultProvider;
    public int ApprovalValidityMonths { get; set; } = 24;
    public int MaxAttempts { get; set; } = 3;
    public string? WebhookSecret { get; set; }
}

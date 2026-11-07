namespace AskVest.IntegrationTests.Infrastructure;

public static class TestComplianceSecrets
{
    public static readonly IReadOnlyDictionary<string, string?> Entries = new Dictionary<string, string?>
    {
        ["Compliance:Kyc:WebhookSecret"] = "test-kyc-webhook-secret-0123456789abcdef",
    };
}

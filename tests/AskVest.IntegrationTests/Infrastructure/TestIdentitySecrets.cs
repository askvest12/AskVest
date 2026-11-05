namespace AskVest.IntegrationTests.Infrastructure;

public static class TestIdentitySecrets
{
    public static readonly IReadOnlyDictionary<string, string?> Entries = new Dictionary<string, string?>
    {
        ["Identity:Secrets:AccessTokenSigningKey"] = "test-access-token-signing-key-0123456789abcdef",
        ["Identity:Secrets:PasswordPepper"] = "test-password-pepper-0123456789abcdef",
        ["Identity:Secrets:OneTimeCodeHashSecret"] = "test-code-hash-secret-0123456789abcdef",
        ["Identity:Secrets:PasswordPepperVersion"] = "1",
    };
}

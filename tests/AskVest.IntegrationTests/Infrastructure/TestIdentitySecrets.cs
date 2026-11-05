namespace AskVest.IntegrationTests.Infrastructure;

public static class TestIdentitySecrets
{
    public static readonly IReadOnlyDictionary<string, string?> Entries = new Dictionary<string, string?>
    {
        ["Identity:Secrets:AccessTokenSigningKey"] = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg9p3d660kKc2vihdihihkQhLHuunz87EFn+VFV0h9LTmhRANCAAT21YdEwg7i6J9UBR/OJ8WEakN2SQYR3bFsdD+DQqGNZloEjonMkumam4mOMulbDokgccTxE0PYcmmue6P2gP9t",
        ["Identity:Secrets:PasswordPepper"] = "test-password-pepper-0123456789abcdef",
        ["Identity:Secrets:OneTimeCodeHashSecret"] = "test-code-hash-secret-0123456789abcdef",
        ["Identity:Secrets:PasswordPepperVersion"] = "1",
    };
}

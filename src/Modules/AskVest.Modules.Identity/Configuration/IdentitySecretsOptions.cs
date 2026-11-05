using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Configuration;

public sealed class IdentitySecretsOptions
{
    public const string SectionName = "Identity:Secrets";

    public string? AccessTokenSigningKey { get; set; }
    public string? AccessTokenSigningKeyPrevious { get; set; }
    public string? PasswordPepper { get; set; }
    public int PasswordPepperVersion { get; set; } = 1;
    public string? OneTimeCodeHashSecret { get; set; }

    public const int MinimumSecretLength = 32;

    public static IValidateOptions<IdentitySecretsOptions> Validator { get; } = new IdentitySecretsOptionsValidator();

    private sealed class IdentitySecretsOptionsValidator : IValidateOptions<IdentitySecretsOptions>
    {
        public ValidateOptionsResult Validate(string? name, IdentitySecretsOptions options)
        {
            var failures = new List<string>();
            void CheckSecret(string? value, string property)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    failures.Add($"{property} is required.");
                }
                else if (value.Length < MinimumSecretLength)
                {
                    failures.Add($"{property} must be at least {MinimumSecretLength} characters.");
                }
            }

            CheckSecret(options.AccessTokenSigningKey, nameof(AccessTokenSigningKey));
            if (!string.IsNullOrWhiteSpace(options.AccessTokenSigningKeyPrevious)
                && options.AccessTokenSigningKeyPrevious.Length < MinimumSecretLength)
            {
                failures.Add($"{nameof(AccessTokenSigningKeyPrevious)} must be at least {MinimumSecretLength} characters when set.");
            }

            CheckSecret(options.PasswordPepper, nameof(PasswordPepper));
            if (options.PasswordPepperVersion < 1)
            {
                failures.Add($"{nameof(PasswordPepperVersion)} must be at least 1.");
            }

            CheckSecret(options.OneTimeCodeHashSecret, nameof(OneTimeCodeHashSecret));

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}

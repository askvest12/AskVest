using System.Text.RegularExpressions;
using AskVest.Modules.Identity.Configuration;
using AskVest.SharedKernel;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Crypto;

public interface IPasswordPolicy
{
    Task<Result> ValidateAsync(string? password, string? email, CancellationToken cancellationToken = default);
}

public sealed partial class PasswordPolicy(
    IOptions<IdentityOptions> options,
    IBreachPasswordChecker breachChecker) : IPasswordPolicy
{
    private static readonly HashSet<string> _deniedPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "1234567890", "12345678910",
        "qwertyuiop", "letmein123", "welcome123", "admin12345", "iloveyou1",
        "askvest123", "askvest2024", "askvest2025", "askvest2026",
    };

    private readonly IdentityOptions _options = options.Value;

    public async Task<Result> ValidateAsync(string? password, string? email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password)
            || password.Length < _options.PasswordMinLength
            || password.Length > _options.PasswordMaxLength)
        {
            return Invalid($"Password must be between {_options.PasswordMinLength} and {_options.PasswordMaxLength} characters.");
        }

        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
        {
            return Invalid("Password must contain at least one letter and one digit.");
        }

        if (_deniedPasswords.Contains(password))
        {
            return Invalid("Password is too common.");
        }

        if (ContainsEmailLocalPart(password, email))
        {
            return Invalid("Password must not contain your email address.");
        }

        if (_options.PasswordBreachCheckEnabled && await breachChecker.IsBreachedAsync(password, cancellationToken))
        {
            return Invalid("Password has appeared in a data breach. Choose a different password.");
        }

        return Result.Success();
    }

    private static bool ContainsEmailLocalPart(string password, string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var atIndex = email.IndexOf('@');
        var local = (atIndex > 0 ? email[..atIndex] : email).Trim().ToLowerInvariant();
        return local.Length >= 3 && password.Contains(local, StringComparison.OrdinalIgnoreCase);
    }

    private static Result Invalid(string message)
        => Result.Failure(new Error("validation.failed", message, ErrorType.Validation));
}

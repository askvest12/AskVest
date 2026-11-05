using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AskVest.Modules.Identity.Crypto;

public interface IBreachPasswordChecker
{
    Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default);
}

public sealed class HaveIBeenPwnedBreachChecker(HttpClient httpClient)
    : IBreachPasswordChecker
{
    private const string ApiUrl = "https://api.pwnedpasswords.com/range/";

    public async Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
    {
        // HIBP k-anonymity API requires SHA-1; not used for password storage.
        var sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password))).ToUpperInvariant();
        var prefix = sha1[..5];
        var suffix = sha1[5..];

        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl + prefix);
        request.Headers.Add("Add-Padding", "true");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(':');
            if (parts.Length == 2 && string.Equals(parts[0], suffix, StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) > 0;
            }
        }

        return false;
    }
}

public sealed class NullBreachPasswordChecker : IBreachPasswordChecker
{
    public Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

public sealed class FailOpenBreachPasswordChecker(IBreachPasswordChecker inner, ILogger<FailOpenBreachPasswordChecker> logger)
    : IBreachPasswordChecker
{
    public async Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.IsBreachedAsync(password, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(exception, "Password breach check failed; allowing password.");
            }

            return false;
        }
    }
}

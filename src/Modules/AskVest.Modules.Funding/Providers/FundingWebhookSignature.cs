using System.Security.Cryptography;
using System.Text;

namespace AskVest.Modules.Funding.Providers;

/// <summary>
/// The shared webhook signature scheme used by the fake adapter and by
/// tests and smoke journeys that must produce valid signatures. HMAC-SHA256
/// over "{timestamp}.{rawBody}" sent as hex in X-Webhook-Signature with the
/// unix seconds timestamp in X-Webhook-Timestamp.
/// </summary>
public static class FundingWebhookSignature
{
    public const string TimestampHeader = "X-Webhook-Timestamp";
    public const string SignatureHeader = "X-Webhook-Signature";

    public static string Sign(string secret, string timestamp, string rawBody)
    {
        var payload = Encoding.UTF8.GetBytes($"{timestamp}.{rawBody}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }

    public static bool Verify(
        string secret,
        string? timestamp,
        string rawBody,
        string? signature,
        TimeSpan tolerance,
        DateTimeOffset now)
    {
        if (timestamp is null
            || signature is null
            || !long.TryParse(timestamp, out var unixSeconds))
        {
            return false;
        }

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if ((now - sentAt).Duration() > tolerance)
        {
            return false;
        }

        var expected = Sign(secret, timestamp, rawBody);
        if (signature.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature));
    }
}

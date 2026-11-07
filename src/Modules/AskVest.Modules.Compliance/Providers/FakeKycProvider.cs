using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AskVest.SharedKernel;

namespace AskVest.Modules.Compliance.Providers;

/// <summary>
/// Deterministic development KYC provider. Webhooks are signed with
/// HMAC-SHA256 over "{timestamp}.{rawBody}" using headers
/// X-Webhook-Timestamp and X-Webhook-Signature.
/// </summary>
internal sealed class FakeKycProvider(string webhookSecret, TimeSpan tolerance) : IKycProvider
{
    private const string TimestampHeader = "X-Webhook-Timestamp";
    private const string SignatureHeader = "X-Webhook-Signature";

    public string Name => "fake";

    public Task<Result<KycSession>> StartVerificationAsync(
        Guid userId,
        string country,
        CancellationToken cancellationToken)
    {
        var reference = $"fake-{Guid.CreateVersion7():N}";
        return Task.FromResult(Result.Success(new KycSession(reference, SessionUrl(reference))));
    }

    public Task<Result<KycSession>> ResumeSessionAsync(
        string providerReference,
        CancellationToken cancellationToken)
        => Task.FromResult(Result.Success(new KycSession(providerReference, SessionUrl(providerReference))));

    public KycWebhookParseResult ParseWebhook(
        string rawBody,
        IReadOnlyDictionary<string, string?> headers,
        DateTimeOffset now)
    {
        if (!headers.TryGetValue(TimestampHeader, out var timestamp)
            || !long.TryParse(timestamp, out var unixSeconds))
        {
            return new KycWebhookParseResult(KycWebhookParseStatus.MissingOrStaleTimestamp, null);
        }

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if ((now - sentAt).Duration() > tolerance)
        {
            return new KycWebhookParseResult(KycWebhookParseStatus.MissingOrStaleTimestamp, null);
        }

        if (!headers.TryGetValue(SignatureHeader, out var signature)
            || !SignaturesEqual(Signature(timestamp, rawBody), signature))
        {
            return new KycWebhookParseResult(KycWebhookParseStatus.InvalidSignature, null);
        }

        return ParseBody(rawBody);
    }

    internal static string SessionUrl(string providerReference)
        => $"https://kyc.example.test/session/{providerReference}";

    internal static string Signature(string timestamp, string rawBody, string secret)
    {
        var payload = Encoding.UTF8.GetBytes($"{timestamp}.{rawBody}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }

    private string Signature(string timestamp, string rawBody)
        => Signature(timestamp, rawBody, webhookSecret);

    private static bool SignaturesEqual(string expected, string? actual)
    {
        if (actual is null || actual.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(actual));
    }

    private static KycWebhookParseResult ParseBody(string rawBody)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            var eventId = root.GetProperty("eventId").GetString();
            var providerReference = root.GetProperty("providerReference").GetString();
            var outcomeText = root.GetProperty("outcome").GetString();
            if (string.IsNullOrWhiteSpace(eventId)
                || string.IsNullOrWhiteSpace(providerReference)
                || outcomeText is not ("approved" or "rejected"))
            {
                return new KycWebhookParseResult(KycWebhookParseStatus.Malformed, null);
            }

            var outcome = outcomeText == "approved" ? KycWebhookOutcome.Approved : KycWebhookOutcome.Rejected;
            string? Optional(string name)
                => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            DateOnly? dateOfBirth = null;
            var dobText = Optional("dateOfBirth");
            if (dobText is not null)
            {
                if (!DateOnly.TryParse(dobText, out var parsed))
                {
                    return new KycWebhookParseResult(KycWebhookParseStatus.Malformed, null);
                }

                dateOfBirth = parsed;
            }

            return new KycWebhookParseResult(
                KycWebhookParseStatus.Ok,
                new KycWebhookEventData(
                    eventId,
                    providerReference,
                    outcome,
                    Optional("rejectionCode"),
                    Optional("verifiedCountry"),
                    Optional("fullName"),
                    dateOfBirth));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new KycWebhookParseResult(KycWebhookParseStatus.Malformed, null);
        }
    }
}

using AskVest.Modules.Compliance.Providers;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Compliance;

public sealed class FakeKycProviderTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private static readonly FakeKycProvider _provider = new(Secret, TimeSpan.FromMinutes(5));

    private static readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Body()
        => """{"eventId":"e1","providerReference":"r1","outcome":"approved","verifiedCountry":"NG","fullName":"A B","dateOfBirth":"1990-01-01"}""";

    private static Dictionary<string, string?> SignedHeaders(string body, long timestamp, string? signature = null)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Webhook-Timestamp"] = timestamp.ToString(),
            ["X-Webhook-Signature"] = signature ?? FakeKycProvider.Signature(timestamp.ToString(), body, Secret),
        };

    [Fact]
    public void Valid_Signature_Parses_Event()
    {
        var body = Body();
        var result = _provider.ParseWebhook(body, SignedHeaders(body, _now.ToUnixTimeSeconds()), _now);

        result.Status.ShouldBe(KycWebhookParseStatus.Ok);
        result.EventData!.Outcome.ShouldBe(KycWebhookOutcome.Approved);
        result.EventData.ProviderReference.ShouldBe("r1");
        result.EventData.DateOfBirth.ShouldBe(new DateOnly(1990, 1, 1));
    }

    [Fact]
    public void Bad_Signature_Is_Rejected()
    {
        var body = Body();
        var result = _provider.ParseWebhook(
            body,
            SignedHeaders(body, _now.ToUnixTimeSeconds(), new string('f', 64)),
            _now);

        result.Status.ShouldBe(KycWebhookParseStatus.InvalidSignature);
    }

    [Fact]
    public void Stale_And_Missing_Timestamps_Are_Rejected()
    {
        var body = Body();
        var stale = _now.AddMinutes(-10).ToUnixTimeSeconds();
        _provider.ParseWebhook(body, SignedHeaders(body, stale), _now)
            .Status.ShouldBe(KycWebhookParseStatus.MissingOrStaleTimestamp);
        _provider.ParseWebhook(body, new Dictionary<string, string?>(), _now)
            .Status.ShouldBe(KycWebhookParseStatus.MissingOrStaleTimestamp);
    }

    [Fact]
    public void Malformed_Body_Is_Rejected()
    {
        var body = "not-json";
        var result = _provider.ParseWebhook(
            body,
            SignedHeaders(body, _now.ToUnixTimeSeconds()),
            _now);

        result.Status.ShouldBe(KycWebhookParseStatus.Malformed);
    }
}

using AskVest.SharedKernel;

namespace AskVest.Modules.Compliance.Providers;

/// <summary>
/// External KYC boundary. The provider hosts all document capture; AskVest
/// stores only the reference, status and decision fields.
/// </summary>
internal interface IKycProvider
{
    string Name { get; }

    Task<Result<KycSession>> StartVerificationAsync(
        Guid userId,
        string country,
        CancellationToken cancellationToken);

    Task<Result<KycSession>> ResumeSessionAsync(
        string providerReference,
        CancellationToken cancellationToken);

    KycWebhookParseResult ParseWebhook(
        string rawBody,
        IReadOnlyDictionary<string, string?> headers,
        DateTimeOffset now);
}

using System.Collections.ObjectModel;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Compliance.Configuration;

public sealed class ComplianceOptions
{
    public const string SectionName = "Compliance";

    public Collection<ComplianceDocumentOptions> Documents { get; } = [];
    public Collection<string> BlockedCountries { get; } = ["KP", "IR", "SY", "CU"];
    public Collection<string> RealMoneyCountries { get; } = [];
    public bool RealMoneyEnabled { get; set; }
    public KycOptions Kyc { get; set; } = new();
    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(5);
    public ScreeningOptions Screening { get; set; } = new();

    public static IValidateOptions<ComplianceOptions> Validator { get; } = new ComplianceOptionsValidator();

    private sealed class ComplianceOptionsValidator : IValidateOptions<ComplianceOptions>
    {
        public ValidateOptionsResult Validate(string? name, ComplianceOptions options)
        {
            var failures = new List<string>();
            void Check(bool condition, string message)
            {
                if (!condition)
                {
                    failures.Add(message);
                }
            }

            var documentTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var document in options.Documents)
            {
                Check(!string.IsNullOrWhiteSpace(document.Type), "Every compliance document needs a type.");
                Check(documentTypes.Add(document.Type), $"Compliance document type '{document.Type}' is repeated.");
                Check(!string.IsNullOrWhiteSpace(document.Version), $"Compliance document '{document.Type}' needs a version.");
                Check(document.Url is not null, $"Compliance document '{document.Type}' needs a link.");
                Check(IsSha256(document.Sha256), $"Compliance document '{document.Type}' needs a SHA-256 content hash.");
                Check(document.RequiredFor is "practice" or "real",
                    $"Compliance document '{document.Type}' must declare RequiredFor 'practice' or 'real'.");
            }

            foreach (var country in options.BlockedCountries.Concat(options.RealMoneyCountries))
            {
                Check(Domain.CountryCodes.IsValid(country), $"Country code '{country}' is not a valid ISO 3166-1 alpha-2 code.");
            }

            var overlap = options.BlockedCountries.Intersect(options.RealMoneyCountries, StringComparer.Ordinal).ToList();
            Check(overlap.Count == 0, $"Countries cannot be both blocked and real-money-enabled: {string.Join(", ", overlap)}.");

            Check(options.Kyc.ApprovalValidityMonths > 0, "Kyc:ApprovalValidityMonths must be positive.");
            Check(options.Kyc.MaxAttempts > 0, "Kyc:MaxAttempts must be positive.");
            Check(!string.IsNullOrWhiteSpace(options.Kyc.WebhookSecret), "Kyc:WebhookSecret is required.");
            Check(System.Text.Encoding.UTF8.GetByteCount(options.Kyc.WebhookSecret ?? string.Empty) >= 32,
                "Kyc:WebhookSecret must be at least 32 bytes.");
            Check(options.WebhookTolerance > TimeSpan.Zero, "WebhookTolerance must be positive.");
            Check(
                Providers.ProviderFactories.IsSupportedKycProvider(options.Kyc.Provider),
                $"Compliance:Kyc:Provider '{options.Kyc.Provider}' is not supported.");
            Check(
                Providers.ProviderFactories.IsSupportedScreeningProvider(options.Screening.Provider),
                $"Compliance:Screening:Provider '{options.Screening.Provider}' is not supported.");

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }

        private static bool IsSha256(string? hash)
            => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }
}

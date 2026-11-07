using AskVest.Modules.Compliance.Domain.Enums;

namespace AskVest.Modules.Compliance.Persistence.Entities;

public sealed class ComplianceProfile
{
    public Guid UserId { get; set; }
    public string? DeclaredCountry { get; set; }
    public KycStatus KycStatus { get; set; }
    public string? KycProvider { get; set; }
    public string? ProviderReference { get; set; }
    public string? VerifiedCountry { get; set; }
    public int AttemptsUsed { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? RejectionCode { get; set; }
    public ScreeningStatus ScreeningStatus { get; set; }
    public DateTimeOffset? ScreenedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Xmin { get; set; }
}

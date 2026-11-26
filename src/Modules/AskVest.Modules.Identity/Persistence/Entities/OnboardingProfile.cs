using AskVest.Modules.Identity.Domain.Enums;

namespace AskVest.Modules.Identity.Persistence.Entities;

public sealed class OnboardingProfile
{
    public Guid UserId { get; set; }
    public OnboardingFamiliarity? InvestingFamiliarity { get; set; }
    public long GoalFlags { get; set; }
    public string? PreferredCurrency { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Xmin { get; set; }
}

using AskVest.Modules.Funding.Domain;

namespace AskVest.Modules.Funding.Persistence.Entities;

public sealed class FundingBeneficiary
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>Provider-side beneficiary reference used for payouts.</summary>
    public string ProviderReference { get; set; } = string.Empty;

    /// <summary>Masked display only; raw details are never stored.</summary>
    public string MaskedDisplay { get; set; } = string.Empty;

    /// <summary>Provider-reported name match at add time.</summary>
    public bool NameVerified { get; set; }

    public BeneficiaryState State { get; set; }

    public DateTimeOffset? ActiveFrom { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }
}

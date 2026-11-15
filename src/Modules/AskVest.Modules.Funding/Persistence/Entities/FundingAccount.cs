namespace AskVest.Modules.Funding.Persistence.Entities;

public sealed class FundingAccount
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>Provider-side account reference, unique across providers.</summary>
    public string ProviderReference { get; set; } = string.Empty;

    /// <summary>
    /// Opaque provider instruction payload shown to the user verbatim.
    /// Never parsed, logged or stored anywhere else.
    /// </summary>
    public string InstructionJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

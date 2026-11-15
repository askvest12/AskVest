namespace AskVest.Modules.Funding.Persistence.Entities;

/// <summary>
/// Opaque provider list cursor per funding provider, so the polling
/// fallback resumes where the last cycle stopped instead of rescanning.
/// </summary>
public sealed class FundingPollCursor
{
    public string ProviderKey { get; set; } = string.Empty;

    public string? Cursor { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

using System.Collections.ObjectModel;

namespace AskVest.Modules.Wallets.Configuration;

/// <summary>
/// Custody provider routes. Only the fake provider exists in this phase; a
/// real adapter registers its provider name here when research picks a
/// vendor. Routes are a list because chain ids contain ':' which the
/// configuration binder cannot express as dictionary keys.
/// </summary>
public sealed class CustodyOptions
{
    public Collection<CustodyRouteOptions> Routes { get; } = [];

    public FakeCustodyOptions Fake { get; set; } = new();

    public PrivyOptions Privy { get; set; } = new();

    /// <summary>Chain id to provider name, last entry wins on duplicates.</summary>
    public IReadOnlyDictionary<string, string> RouteProviders
        => Routes
            .Where(route => !string.IsNullOrWhiteSpace(route.ChainId))
            .GroupBy(route => route.ChainId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Provider ?? string.Empty,
                StringComparer.Ordinal);
}

public sealed class CustodyRouteOptions
{
    public string ChainId { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;
}

/// <summary>
/// Deterministic fake custody settings. The scenario is mutable at runtime
/// so the diagnostics probe and tests can arm it; the fake is refused in
/// Production.
/// </summary>
public sealed class FakeCustodyOptions
{
    public string Scenario { get; set; } = "normal";

    public int CompletionDelaySeconds { get; set; } = 2;

    /// <summary>
    /// Seconds a pending deposit takes to report final, mirroring chain
    /// confirmations.
    /// </summary>
    public int PendingFinalitySeconds { get; set; } = 30;
}

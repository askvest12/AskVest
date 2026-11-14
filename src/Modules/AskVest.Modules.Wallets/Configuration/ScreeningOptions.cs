using System.Collections.ObjectModel;

namespace AskVest.Modules.Wallets.Configuration;

/// <summary>
/// Address screening configuration. Only the fake provider exists in this
/// phase; a real adapter registers its provider name here when research
/// picks a vendor.
/// </summary>
public sealed class ScreeningOptions
{
    public string Provider { get; set; } = "fake";

    public FakeScreeningOptions Fake { get; set; } = new();
}

/// <summary>
/// Deterministic fake screening settings. The scenario and hit markers are
/// mutable at runtime so the diagnostics probe and tests can arm them; the
/// fake is refused in Production.
/// </summary>
public sealed class FakeScreeningOptions
{
    public string Scenario { get; set; } = "normal";

    public Collection<string> HitMarkers { get; } = [];
}

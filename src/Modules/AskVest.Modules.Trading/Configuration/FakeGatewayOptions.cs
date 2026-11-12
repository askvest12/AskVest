namespace AskVest.Modules.Trading.Configuration;

/// <summary>
/// Deterministic fake gateway settings. The scenario is mutable at runtime so
/// the diagnostics probe can arm it; the fake is refused in Production.
/// </summary>
public sealed class FakeGatewayOptions
{
    public string Scenario { get; set; } = "normal";

    public int FillDelaySeconds { get; set; } = 2;

    public int SubmitDelaySeconds { get; set; } = 3;

    public decimal SpreadPercent { get; set; } = 0.5m;

    public decimal GatewayFeeAmount { get; set; }
}
